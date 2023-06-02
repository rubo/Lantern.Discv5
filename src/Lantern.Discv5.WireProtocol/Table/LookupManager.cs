using System.Diagnostics;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Packet;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Table;

public class LookupManager : ILookupManager
{
    private readonly IRoutingTable _routingTable;
    private readonly IPacketManager _packetManager;
    private readonly TableOptions _tableOptions;
    private readonly ILogger<LookupManager> _logger;
    private readonly List<PathBucket> _pathBuckets;

    public LookupManager(IRoutingTable routingTable, IPacketManager packetManager, ILoggerFactory loggerFactory, TableOptions tableOptions)
    {
        _routingTable = routingTable;
        _packetManager = packetManager;
        _logger = loggerFactory.CreateLogger<LookupManager>();
        _tableOptions = tableOptions;
        _pathBuckets = new List<PathBucket>();
    }
    
    public bool IsLookupInProgress => _pathBuckets.Any(bucket => !bucket.IsComplete);

    public PathBucket GetBucketByNodeId(byte[] nodeId) => _pathBuckets.First(bucket => bucket.TargetNodeId.SequenceEqual(nodeId));
    
    public List<PathBucket> GetCompletedBuckets() => _pathBuckets.Where(bucket => bucket.IsComplete).ToList();
    
    public async Task<List<NodeTableEntry>> LookupAsync(byte[] targetNodeId)
    {
        await StartLookup(targetNodeId);

        var bucketCompletionTasks = _pathBuckets.Select(async bucket =>
            {
                var completedTask = await Task.WhenAny(bucket.CompletionSource.Task, Task.Delay(_tableOptions.LookupTimeoutMilliseconds));

                if (completedTask == bucket.CompletionSource.Task)
                {
                    _logger.LogInformation("Bucket {BucketIndex} completed successfully", bucket.Index);
                }
                else
                {
                    _logger.LogInformation("Bucket {BucketIndex} timed out", bucket.Index);
                }

                return completedTask == bucket.CompletionSource.Task;
            })
            .ToList();

        await Task.WhenAll(bucketCompletionTasks);
    
        var completedBuckets = GetCompletedBuckets();
    
        var result = completedBuckets
            .SelectMany(bucket => bucket.DiscoveredNodes)
            .Distinct()
            .OrderBy(node => TableUtility.Log2Distance(node.Id, targetNodeId))
            .Take(TableConstants.BucketSize)
            .ToList();

        return result;
    }

    public async Task StartLookup(byte[] targetNodeId)
    {
        _logger.LogInformation("Starting lookup for target node {NodeID}", Convert.ToHexString(targetNodeId));

        var initialNodes = _routingTable.GetClosestNodes(targetNodeId)
            .Take(_tableOptions.ConcurrencyParameter)
            .ToList();
        
        _logger.LogInformation("Initial nodes count {InitialNodesCount}", initialNodes.Count);
        
        var pathBuckets = PartitionInitialNodesNew(initialNodes, targetNodeId);
        
        _pathBuckets.AddRange(pathBuckets);
        _logger.LogInformation("Total number of path buckets {PathBucketCount}", pathBuckets.Count);
        
        foreach (var pathBucket in _pathBuckets)
        {
            foreach (var node in pathBucket.Responses)
            {
                await QuerySelfNode(pathBucket, node.Key, false);
            }
        }
    }

    public async Task ContinueLookup(List<NodeTableEntry> nodes, byte[] senderNode, int expectedResponses)
    {
        foreach (var bucket in _pathBuckets.Where(bucket => bucket.Responses.Any(node => node.Key.SequenceEqual(senderNode))))
        {
            if (bucket.QueriedNodes.Count < TableConstants.BucketSize)
            {
                if (bucket.ExpectedResponses.ContainsKey(senderNode))
                {
                    bucket.ExpectedResponses[senderNode]--;
                }
                else
                {
                    bucket.ExpectedResponses.Add(senderNode, expectedResponses - 1);
                }
            
                _logger.LogInformation("Expecting {ExpectedResponses} more responses from node {NodeId} in QueryClosestNodes in bucket {BucketIndex}", bucket.ExpectedResponses[senderNode], Convert.ToHexString(senderNode), bucket.Index);
            
                if (!bucket.QueriedNodes.Contains(senderNode))
                {
                    bucket.QueriedNodes.Add(senderNode);
                }
            
                _logger.LogInformation("Queried {QueriedNodes} nodes so far in bucket {BucketIndex}", bucket.QueriedNodes.Count, bucket.Index);
                _logger.LogDebug("Discovered {DiscoveredNodes} nodes so far in bucket {BucketIndex}", bucket.DiscoveredNodes.Count, bucket.Index);
            
                if (nodes.Count > 0)
                {
                    _logger.LogDebug("Received {NodesCount} nodes from node {NodeId} in bucket {BucketIndex}", nodes.Count, Convert.ToHexString(senderNode), bucket.Index); 
                    await UpdatePathBucketNew(bucket, nodes, senderNode, false);
                }
                else
                {
                    // If node replies with 0 nodes, vary the distance and try again
                    _logger.LogDebug("Received no nodes from node {NodeId}. Varying distances", Convert.ToHexString(senderNode));
                    await QuerySelfNode(bucket, senderNode, true);
                }
            }
            else
            {
                // Move this into LookupAsync
                bucket.IsComplete = true;
                bucket.CompletionSource.TrySetResult(true);
                _logger.LogInformation("Lookup in bucket {BucketIndex} is complete. ", bucket.Index);
            }
        }
    }

    private async Task UpdatePathBucketNew(PathBucket bucket, List<NodeTableEntry> nodes, byte[] senderNodeId, bool varyDistance)
    {
        var sortedNodes = nodes.OrderBy(nodeEntry => TableUtility.Log2Distance(nodeEntry.Id, bucket.TargetNodeId)).ToList();
        
        foreach (var node in sortedNodes)
        {
            bucket.Responses[senderNodeId].Add(node);
            
            if (bucket.Responses.ContainsKey(node.Id))
            {
                bucket.Responses[node.Id] = new List<NodeTableEntry>();
            }
            else
            {
                bucket.Responses.Add(node.Id, new List<NodeTableEntry>());
            }
            
            bucket.DiscoveredNodes.Add(node);
        }

        bucket.Responses[senderNodeId].Sort((node1, node2) => TableUtility.Log2Distance(node1.Id, bucket.TargetNodeId).CompareTo(TableUtility.Log2Distance(node2.Id, bucket.TargetNodeId)));
        bucket.DiscoveredNodes.Sort((node1, node2) => TableUtility.Log2Distance(node1.Id, bucket.TargetNodeId).CompareTo(TableUtility.Log2Distance(node2.Id, bucket.TargetNodeId)));

        await QueryClosestNodes(bucket, senderNodeId, varyDistance);
    }

    private async Task QueryClosestNodes(PathBucket bucket, byte[] senderNodeId, bool varyDistance)
    {
        var nodesToQuery = bucket.Responses[senderNodeId].Take(_tableOptions.ConcurrencyParameter).ToList();
        
        if (bucket.ExpectedResponses[senderNodeId] != 0)
        {
            return;
        }
        
        foreach (var node in nodesToQuery)
        {
            _logger.LogDebug("Querying {NodesCount} nodes received from node {NodeId} in bucket {BucketIndex}", nodesToQuery.Count, Convert.ToHexString(senderNodeId), bucket.Index);
            
            // It should first do a liveness check by sending a ping packet
            await _packetManager.SendFindNodePacket(node.Record, bucket.TargetNodeId, varyDistance);
        }
    }
    
    private async Task QuerySelfNode(PathBucket bucket, byte[] senderNodeId, bool varyDistance)
    {
        var node = _routingTable.GetNodeEntry(senderNodeId);
            
        if(node == null)
            return;

        if (bucket.ExpectedResponses[senderNodeId] != 0 && !varyDistance)
        {
            _logger.LogDebug("Waiting for {ExpectedResponses} more responses from node {NodeId} in QuerySelfNode", bucket.ExpectedResponses[senderNodeId], Convert.ToHexString(senderNodeId));
            return;
        }

        if (bucket.QueriedNodes.Count < TableConstants.BucketSize)
        {
            await _packetManager.SendFindNodePacket(node.Record, bucket.TargetNodeId, varyDistance);
        }
    }
    
    private List<PathBucket> PartitionInitialNodesNew(IReadOnlyList<NodeTableEntry> initialNodes, byte[] targetNodeId)
    {
        var bucketCount = Math.Min(initialNodes.Count, _tableOptions.LookupParallelism);
        var pathBuckets = new List<PathBucket>();

        for (var i = 0; i < bucketCount; i++)
        {
            pathBuckets.Add(new PathBucket(targetNodeId, i));
        }

        for (var i = 0; i < initialNodes.Count; i++)
        {
            pathBuckets[i % bucketCount].Responses.Add(initialNodes[i].Id, new List<NodeTableEntry>());
            pathBuckets[i % bucketCount].ExpectedResponses.Add(initialNodes[i].Id, 0);
        }

        return pathBuckets;
    }
}