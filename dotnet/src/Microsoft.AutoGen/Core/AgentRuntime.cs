// Copyright (c) Microsoft Corporation. All rights reserved.
// AgentRuntime.cs
using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AutoGen.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.AutoGen.Core;

/// <summary>
/// An InMemory single-process implementation of <see cref="IAgentRuntime"/>.
/// </summary>
/// <remarks>
/// Responsible for message routing and delivery. 
/// </remarks>
/// <param name="hostApplicationLifetime">The application lifetime.</param>
/// <param name="serviceProvider">The service provider.</param>
/// <param name="configuredAgentTypes">The configured agent types.</param>
public class AgentRuntime(
    IHostApplicationLifetime hostApplicationLifetime,
    IServiceProvider serviceProvider,
    [FromKeyedServices("AgentTypes")] IEnumerable<Tuple<string, System.Type>> configuredAgentTypes,
    ILogger<AgentRuntime> logger) :
    AgentRuntimeBase(
        hostApplicationLifetime,
        serviceProvider,
        configuredAgentTypes,
        logger)
{
    private readonly ConcurrentDictionary<string, AgentState> _agentStates = new();
    private readonly ConcurrentDictionary<string, List<Subscription>> _subscriptionsByAgentType = new();
    private readonly ConcurrentDictionary<string, List<string>> _subscriptionsByTopic = new();
    private readonly ConcurrentDictionary<Guid, IDictionary<string, string>> _subscriptionsByGuid = new();
    private readonly IRegistry _registry = serviceProvider.GetRequiredService<IRegistry>();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RpcResponse>> _pendingRequests = new();
    private static readonly TimeSpan s_agentResponseTimeout = TimeSpan.FromSeconds(300);
    private new readonly ILogger<AgentRuntime> _logger = logger;

    /// <inheritdoc />
    public override async ValueTask RegisterAgentTypeAsync(RegisterAgentTypeRequest request, CancellationToken cancellationToken = default)
    {
        await _registry.RegisterAgentTypeAsync(request, this);
    }
    /// <inheritdoc />
    public override async ValueTask<RpcResponse> SendMessageAsync(IMessage message, AgentId agentId, AgentId? agent = null, CancellationToken? cancellationToken = default)
    {
        var request = new RpcRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            Source = agent,
            Target = agentId,
            Payload = new Payload { Data = Any.Pack(message).ToByteString() }
        };
        var response = await InvokeRequestAsync(request).ConfigureAwait(false);
        return response;
    }
    /// <inheritdoc />
    public override ValueTask SaveStateAsync(AgentState value, CancellationToken cancellationToken = default)
    {
        var agentId = value.AgentId ?? throw new InvalidOperationException("AgentId is required when saving AgentState.");
        var response = _agentStates.AddOrUpdate(agentId.ToString(), value, (key, oldValue) => value);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask<AgentState> LoadStateAsync(AgentId agentId, CancellationToken cancellationToken = default)
    {
        _agentStates.TryGetValue(agentId.ToString(), out var state);
        if (state is not null && state.AgentId is not null)
        {
            return new ValueTask<AgentState>(state);
        }
        else
        {
            throw new KeyNotFoundException($"Failed to read AgentState for {agentId}.");
        }
    }
    /// <inheritdoc />
    public override async ValueTask RuntimeSendRequestAsync(IAgent agent, RpcRequest request, CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString();
        _pendingClientRequests[requestId] = ((Agent)agent, request.RequestId);
        request.RequestId = requestId;
        await _mailbox.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override ValueTask RuntimeSendResponseAsync(RpcResponse response, CancellationToken cancellationToken = default)
    {
        return _mailbox.Writer.WriteAsync(new Message { Response = response }, cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask RuntimeWriteMessage(Message message, CancellationToken cancellationToken = default)
    {
        return _mailbox.Writer.WriteAsync(message, cancellationToken);
    }
    public override async ValueTask<AddSubscriptionResponse> AddSubscriptionAsync(AddSubscriptionRequest subscription, CancellationToken cancellationToken = default)
    {
        var topic = subscription.Subscription.TypeSubscription.TopicType;
        var agentType = subscription.Subscription.TypeSubscription.AgentType;
        var id = Guid.NewGuid();
        subscription.Subscription.Id = id.ToString();
        var sub = new Dictionary<string, string> { { topic, agentType } };
        _subscriptionsByGuid.GetOrAdd(id, static _ => new Dictionary<string, string>()).Add(topic, agentType);
        _subscriptionsByAgentType.GetOrAdd(key: agentType, _ => []).Add(subscription.Subscription);
        _subscriptionsByTopic.GetOrAdd(topic, _ => []).Add(agentType);
        var response = new AddSubscriptionResponse
        {
            RequestId = subscription.RequestId,
            Error = "",
            Success = true
        };
        return response;
    }
    public override async ValueTask<RemoveSubscriptionResponse> RemoveSubscriptionAsync(RemoveSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(request.Id, out var id))
        {
            var removeSubscriptionResponse = new RemoveSubscriptionResponse
            {
                Error = "Invalid subscription ID",
                Success = false
            };
            return removeSubscriptionResponse;
        }
        if (_subscriptionsByGuid.TryGetValue(id, out var sub))
        {
            foreach (var (topic, agentType) in sub)
            {
                if (_subscriptionsByTopic.TryGetValue(topic, out var innerAgentTypes))
                {
                    while (innerAgentTypes.Remove(agentType))
                    {
                        //ensures all instances are removed
                    }
                    _subscriptionsByTopic.AddOrUpdate(topic, innerAgentTypes, (_, _) => innerAgentTypes);
                }
                var toRemove = new List<Subscription>();
                if (_subscriptionsByAgentType.TryGetValue(agentType, out var innerSubscriptions))
                {
                    foreach (var subscription in innerSubscriptions)
                    {
                        if (subscription.Id == id.ToString())
                        {
                            toRemove.Add(subscription);
                        }
                    }
                    foreach (var subscription in toRemove) { innerSubscriptions.Remove(subscription); }
                    _subscriptionsByAgentType.AddOrUpdate(agentType, innerSubscriptions, (_, _) => innerSubscriptions);
                }
            }
            _subscriptionsByGuid.TryRemove(id, out _);
        }
        var response = new RemoveSubscriptionResponse
        {
            Error = "",
            Success = true
        };
        return response;
    }

    public ValueTask<List<Subscription>> GetSubscriptionsAsync(System.Type type)
    {
        if (_subscriptionsByAgentType.TryGetValue(type.Name, out var subscriptions))
        {
            return new ValueTask<List<Subscription>>(subscriptions);
        }
        return new ValueTask<List<Subscription>>([]);
    }
    public override ValueTask<List<Subscription>> GetSubscriptionsAsync(GetSubscriptionsRequest request, CancellationToken cancellationToken = default)
    {
        var subscriptions = new List<Subscription>();
        foreach (var (_, value) in _subscriptionsByAgentType)
        {
            subscriptions.AddRange(value);
        }
        return new ValueTask<List<Subscription>>(subscriptions);
    }
    public override async ValueTask DispatchRequestAsync(RpcRequest request)
    {
        var requestId = request.RequestId;
        if (request.Target is null)
        {
            throw new InvalidOperationException($"Request message is missing a target. Message: '{request}'.");
        }
        var agentId = request.Target;
        await InvokeRequestDelegate(_mailbox, request, async request =>
        {
            return await InvokeRequestAsync(request).ConfigureAwait(true);
        }).ConfigureAwait(false);
    }
    public override void DispatchResponse(RpcResponse response)
    {
        if (!_pendingRequests.TryRemove(response.RequestId, out var completion))
        {
            _logger.LogWarning("Received response for unknown request id: {RequestId}.", response.RequestId);
            return;
        }
        // Complete the request.
        completion.SetResult(response);
    }
    public async ValueTask<RpcResponse> InvokeRequestAsync(RpcRequest request, CancellationToken cancellationToken = default)
    {
        var agentId = request.Target;
        // get the agent
        var agent = GetOrActivateAgent(agentId);

        // Proxy the request to the agent.
        var originalRequestId = request.RequestId;
        var completion = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests.TryAdd(request.RequestId, completion);
        //request.RequestId = Guid.NewGuid().ToString();
        agent.ReceiveMessage(new Message() { Request = request });
        // Wait for the response and send it back to the caller.
        var response = await completion.Task.WaitAsync(s_agentResponseTimeout);
        response.RequestId = originalRequestId;
        return response;
    }
    private static async Task InvokeRequestDelegate(Channel<object> mailbox, RpcRequest request, Func<RpcRequest, Task<RpcResponse>> func)
    {
        try
        {
            var response = await func(request);
            response.RequestId = request.RequestId;
            await mailbox.Writer.WriteAsync(new Message { Response = response }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await mailbox.Writer.WriteAsync(new Message { Response = new RpcResponse { RequestId = request.RequestId, Error = ex.Message } }).ConfigureAwait(false);
        }
    }
}
