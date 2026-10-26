# Smart-X Data Structures and Algorithms

[Back to the README](../README.md)

This document explains the implemented trees, graph representation and minimum spanning tree algorithm, their application roles and their contribution to efficiency. Code excerpts come from Smart-X; worked examples illustrate the resulting behaviour.

## Contents

1. [Purpose and application context](#1-purpose-and-application-context)
2. [AVL trees and historical telemetry](#2-avl-trees-and-historical-telemetry)
3. [Weighted graph representation](#3-weighted-graph-representation)
4. [Breadth-first search and reachability](#4-breadth-first-search-and-reachability)
5. [Prim's algorithm and the priority queue](#5-prims-algorithm-and-the-priority-queue)
6. [Gateway routes and topology changes](#6-gateway-routes-and-topology-changes)
7. [Performance and scalability](#7-performance-and-scalability)
8. [Verification](#8-verification)
9. [References](#9-references)

## 1. Purpose and application context

Smart-X combines sensor registration, telemetry ingestion, command processing and network topology in a .NET 10 API and MVC application. Network Topology displays devices and weighted connections, identifies unavailable or disconnected devices, and highlights a selected device's route to the gateway.

| Structure or algorithm | Specific role | Contribution |
|---|---|---|
| Per-sensor AVL tree | Index timestamped telemetry | Answers bounded historical queries without scanning every sensor's readings |
| Weighted, undirected adjacency-list graph | Store devices and bidirectional connection costs | Supports neighbour traversal and link management without a dense matrix |
| Queue and visited set for BFS | Find the gateway's reachable component | Distinguishes connectivity from availability and prevents repeated visits |
| Priority queue for Prim's algorithm | Select the cheapest candidate connection | Builds a minimum-cost spanning tree for the gateway component |
| Parent lookup over the selected tree | Follow a device's route to the gateway | Produces an ordered path and accumulated cost |

The AVL tree supports historical investigation of device behaviour. It does not store the network or calculate routes. The spanning tree produced by Prim is the tree directly used by Network Topology. These are distinct uses of trees within the complete application.

Smart-X simulates devices and routing decisions; it does not forward packets through physical mesh hardware. Connection costs are configurable weights, not measured battery consumption or electrical power savings.

## 2. AVL trees and historical telemetry

### Role and design choice

[TelemetryHistoryTree.cs](../SmartX.Api/DataStructures/TelemetryHistoryTree.cs) implements a custom AVL tree. [TelemetryService.cs](../SmartX.Api/Services/TelemetryService.cs) maintains one tree per sensor and queries that index when retrieving a sensor's historical range.

A list would require scanning to find interval matches, or shifting elements to maintain timestamp order. An ordinary binary search tree can become a chain when readings arrive in increasing order. AVL balancing maintains logarithmic height through rotations, preserving search order while supporting efficient insertion and removal (Silva, 2026).

### Keys and balancing

Each node has a composite key:

```csharp
public (DateTimeOffset Timestamp, long Sequence) Key { get; } = (reading.Timestamp, sequence);
```

The timestamp determines chronological order. The increasing sequence number distinguishes readings with identical timestamps. Late readings are positioned according to their timestamps rather than their arrival positions.

This excerpt handles a left-heavy subtree:

```csharp
UpdateHeight(node);
var balance = GetHeight(node.Left) - GetHeight(node.Right);

if (balance > 1)
{
    if (GetHeight(node.Left!.Left) < GetHeight(node.Left.Right))
    {
        node.Left = RotateLeft(node.Left);
    }

    return RotateRight(node);
}
```

A left-left imbalance needs one right rotation. A left-right imbalance first rotates the child left, then the parent right. The corresponding right-heavy branch handles right-right and right-left cases. Insertions and removals rebalance affected ancestors and update their heights. Removing a node with two children uses its in-order successor.

For example, inserting 10:00, 10:01 and 10:02 into an ordinary tree creates a rightward chain. A left rotation places 10:01 above the other entries without changing their logical order.

### Bounded range queries

The traversal prunes subtrees outside the inclusive interval:

```csharp
if (node.Key.Timestamp > to)
{
    return CollectRange(node.Left, from, to, limit, readings);
}

if (node.Key.Timestamp < from)
{
    return CollectRange(node.Right, from, to, limit, readings);
}
```

Within the interval, the right subtree is visited first to collect the newest readings. Traversal stops after the limit and an additional match establish `HasMore`. The collected list is reversed before returning it, presenting the selected readings chronologically.

Suppose readings exist at 10:00, 10:01, 10:01 and 10:02. A query for 10:01–10:02 with a limit of two returns the later-sequence 10:01 reading followed by 10:02. `HasMore` is true because the other 10:01 reading also matches. Both duplicate timestamps remain stored.

[TelemetryController.cs](../SmartX.Api/Controllers/TelemetryController.cs) exposes the range endpoint with a response limit of 1–1,000 readings. The [history view](../SmartX.Web/Views/Sensors/History.cshtml) supplies UTC filters, a result limit and validation feedback.

### Retention and efficiency

Shared history retains at most 10,000 readings across all sensors. A FIFO queue tracks arrival order. Eviction removes the oldest-arriving reading from shared history and removes its exact timestamp/sequence key from the corresponding AVL tree. Empty indexes are removed. Retention is not an age cutoff: a newly received reading with an old timestamp can remain while an earlier arrival is evicted.

For `n` retained sensor readings, insertion and removal take `O(log n)` time. A range query returning up to `k` readings takes `O(log n + k)` time, including at most one extra match for truncation detection and an `O(k)` reversal. Tree storage is `O(n)`; query recursion and output require `O(log n + k)` space.

These bounds describe the per-sensor index. Ingestion also updates shared collections under a lock, and the all-sensor history path still uses shared sorted history. The index does not make every telemetry operation logarithmic.

## 3. Weighted graph representation

### Role and design choice

[InMemoryMeshRepository.cs](../SmartX.Api/Repositories/InMemoryMeshRepository.cs) stores nodes and their neighbours as dictionaries:

```csharp
private readonly Dictionary<Guid, MeshNode> _nodes = [];
private readonly Dictionary<Guid, Dictionary<Guid, double>> _adjacency = [];
```

Vertices represent the gateway and sensors. Edges represent bidirectional connections with finite costs greater than zero and no more than 1,000,000. An adjacency list stores existing connections in `O(V + E)` space, compared with `O(V²)` entries for an adjacency matrix. This suits a mesh where devices have relatively few neighbours. Dictionary lookup is normally close to constant time, depending on hashing behaviour (Microsoft, 2026a).

### Symmetric links and snapshots

Adding a connection stores both directions:

```csharp
_adjacency[firstNodeId].Add(secondNodeId, cost);
_adjacency[secondNodeId].Add(firstNodeId, cost);
```

Updates and removals also affect both entries. Validation rejects self-links, unknown endpoints, duplicate additions and invalid costs. A Gateway–ENV connection of cost 1 therefore creates two adjacency entries representing one undirected edge.

Mutations and snapshot creation are synchronised. `GetGraph` copies nodes and neighbour lists into read-only results. Algorithms process the snapshot outside the repository lock, and later edits do not mutate earlier results. Copying costs `O(V + E)` time and space, which contributes to actual request cost.

### Application integration

[MeshTopologyService.cs](../SmartX.Api/Services/MeshTopologyService.cs) synchronises registered sensors and projects availability onto the graph. The gateway remains available. A sensor is available while its last heartbeat is no more than 30 seconds old.

Offline devices retain configured links, so recovery does not require recreating connections. Traversal and routing exclude unavailable nodes. An online device can still be disconnected when it has no usable path to the gateway.

## 4. Breadth-first search and reachability

[MeshTraversal.cs](../SmartX.Api/DataStructures/MeshTraversal.cs) uses a FIFO queue for pending nodes and a hash set for discovered nodes:

```csharp
while (pending.TryDequeue(out var nodeId))
{
    reachable.Add(nodeId);
    foreach (var connection in graph.Adjacency[nodeId])
    {
        if (graph.Nodes[connection.NodeId].IsAvailable && visited.Add(connection.NodeId))
        {
            pending.Enqueue(connection.NodeId);
        }
    }
}
```

Marking a node when enqueued prevents cycles from adding it repeatedly. The queue explores neighbours in breadth-first order; undiscovered nodes are returned as unreachable. This queue-and-visited approach follows the standard BFS principle illustrated by Sedgewick and Wayne (2026a).

If Gateway connects to ENV and PWR, and ENV connects to ACT, BFS discovers ENV and PWR before exploring their neighbours. ACT can be reached through ENV. Removing all usable paths to ACT makes it unreachable even if its heartbeat remains current.

BFS supplies connectivity classification. It does not minimise weighted cost: both a cost-8 edge and a cost-1 edge count as one connection. Its queue avoids repeatedly rescanning the complete node collection to find the next unexplored node.

Worst-case traversal is `O(V + E)` time and `O(V)` auxiliary space. Each reachable node is enqueued once and its adjacency entries are inspected once. Scanning all nodes to form the unreachable list adds `O(V)` work.

## 5. Prim's algorithm and the priority queue

### Role and selection process

[MeshMinimumSpanningTree.cs](../SmartX.Api/DataStructures/MeshMinimumSpanningTree.cs) implements lazy Prim. From the gateway, it selects the cheapest candidate edge joining the growing tree to an unvisited available node. Obsolete candidates remain queued until removed and discarded. This is the lazy selection approach described by Sedgewick and Wayne (2026b).

```csharp
var candidates = new PriorityQueue<MeshTreeEdge, (double Cost, Guid From, Guid To)>();
```

Cost is the primary priority; endpoint GUIDs resolve ties deterministically for fixed node IDs. The implementation does not depend on FIFO tie handling. The .NET queue uses an array-backed quaternary min-heap and removes the lowest priority first (Microsoft, 2026b).

```csharp
while (candidates.TryDequeue(out var edge, out _))
{
    if (visited.Contains(edge.ToNodeId))
    {
        continue;
    }

    edges.Add(edge);
    totalCost += edge.Cost;
    Visit(edge.ToNodeId);
}
```

`Visit` marks the new node and enqueues edges to available, unvisited neighbours. Discarding an edge whose destination is already visited prevents cycles. The algorithm repeatedly selects the cheapest crossing edge, producing a minimum spanning tree for the gateway component. It is a custom Prim implementation using a library priority queue, not a custom heap or indexed decrease-key implementation.

### Worked demo example

ENV, ACT and PWR abbreviate the environmental, actuator and power sensors in the demo:

| Connection | Cost | Selected initially |
|---|---:|---|
| Gateway–ENV | 1 | Yes |
| ENV–ACT | 2 | Yes |
| ACT–PWR | 3 | Yes |
| ENV–PWR | 6 | No |
| Gateway–PWR | 8 | No |

Prim selects costs 1, 2 and 3 in sequence, connecting four nodes with three edges and a total tree cost of **6**. Adding either remaining edge would introduce a cycle. For a connected component of `r` nodes, the selected tree has `r − 1` edges. Nodes outside the gateway component are reported as unreachable; separate trees are not built for disconnected components.

### Efficiency and interpretation

The lazy queue can hold `O(E)` candidates. Logarithmic heap insertion and removal give `O(V + E log(E + 1))` time, including node reporting, and `O(V + E)` auxiliary space. The `+1` accommodates an empty edge set. A plain unsorted candidate list would require a linear scan for each minimum selection.

An MST minimises the sum of selected edges, not every individual route. For example, Gateway–A cost 2, A–B cost 2 and Gateway–B cost 3 produce an MST costing 4. B's tree route costs 4 although its direct edge costs 3. Smart-X follows the spanning tree rather than independently calculating shortest paths.

## 6. Gateway routes and topology changes

### Following the selected tree

[MeshRouting.cs](../SmartX.Api/DataStructures/MeshRouting.cs) creates a child-to-parent lookup and follows it towards the root:

```csharp
var parents = tree.Edges.ToDictionary(edge => edge.ToNodeId);
var path = new List<Guid> { startNodeId };
var nodeId = startNodeId;
var totalCost = 0d;
while (nodeId != tree.StartNodeId)
{
    var edge = parents[nodeId];
    totalCost += edge.Cost;
    nodeId = edge.FromNodeId;
    path.Add(nodeId);
}
```

Initially, PWR follows PWR → ACT → ENV → Gateway, costing `3 + 2 + 1 = 6`. Selecting the gateway returns a single-node path costing zero. A known unreachable device returns an empty path with a null cost.

The walk takes `O(h)` time for `h` edges. However, membership checks and parent-dictionary creation add `O(V)` work. The complete method takes `O(V + h)` time with space bounded by `O(V + h)`, rather than only the cost of walking the path.

### Failure and recovery

After ENV is paused and its heartbeat expires, its links remain stored but cannot participate. Prim selects Gateway–PWR at cost 8 and PWR–ACT at cost 3. The remaining tree costs **11**, while PWR's route costs **8**. Tree cost includes every selected connection; route cost includes only the selected device's path.

Resuming ENV restores availability after telemetry resumes, allowing the original lower-cost tree on the next calculation. Link changes also affect subsequent calculations. The service recomputes the tree from the current graph rather than incrementally repairing a cached tree.

### Operator interface

The [topology view](../SmartX.Web/Views/Topology/Index.cshtml) provides identifier search, link management and simulation controls. Its [network partial](../SmartX.Web/Views/Topology/_Network.cshtml) renders an SVG diagram showing weights, availability and the selected route.

[topology.js](../SmartX.Web/wwwroot/js/topology.js) schedules another refresh five seconds after the previous refresh completes. Requests have a ten-second timeout; failures preserve the last successful display with retry feedback. Heartbeat expiry, request duration and refresh scheduling mean an offline transition does not necessarily appear exactly five seconds after a pause.

The page combines multiple API requests that can use separate snapshots. During simulation, their responses need not describe one atomic instant. This is a periodically refreshed management view, not a guarantee of real-time delivery.

## 7. Performance and scalability

`n` is one sensor's retained reading count, `k` is the result count, `V` is the vertex count, `E` is the undirected edge count and `h` is the path length. Dictionary costs assume expected hashing behaviour.

| Operation | Time | Space or storage |
|---|---|---|
| AVL insertion/removal | `O(log n)` | Tree `O(n)`; recursion `O(log n)` |
| Bounded AVL range query | `O(log n + k)` | `O(log n + k)` |
| Repository link lookup/change after validation | Expected `O(1)` | Two adjacency entries per link |
| Graph snapshot | `O(V + E)` | `O(V + E)` |
| BFS | `O(V + E)` | `O(V)` auxiliary |
| Lazy Prim | `O(V + E log(E + 1))` | `O(V + E)` auxiliary |
| Route extraction from an existing tree | `O(V + h)` | `O(V + h)` upper bound |

End-to-end requests additionally synchronise sensors, copy snapshots, project availability, transfer JSON and render results. A route endpoint computes the MST before extracting the path; the routing-method bound alone is not its full request cost.

Identifier search scans identifiers and may sort partial matches. It is not a dictionary lookup by identifier. Polling repeats graph and algorithm work, while locks can introduce contention under concurrent load.

The 10,000-reading retention cap bounds historical storage. No production-scale mesh capacity or throughput benchmark is claimed. Functional tests and algorithmic bounds support correctness and expected growth, not guaranteed response times. Telemetry indexes and mesh state are held in memory and are lost on API restart.

## 8. Verification

| Area | Existing tests | Evidence |
|---|---|---|
| AVL balance and ordering | [Tree tests](../SmartX.Api.Tests/TelemetryHistoryTreeTests.cs), [removal tests](../SmartX.Api.Tests/TelemetryHistoryTreeRemovalTests.cs) | Ordered insertions, rotations, composite keys and removal |
| Historical queries | [Range tests](../SmartX.Api.Tests/TelemetryHistoryTreeRangeTests.cs), [index tests](../SmartX.Api.Tests/TelemetryHistoryIndexTests.cs) | Inclusive limits, duplicate timestamps, late arrivals, retention, sensor isolation and concurrency |
| Graph consistency | [Repository tests](../SmartX.Api.Tests/MeshRepositoryTests.cs) | Symmetric links, validation and snapshots |
| Reachability | [Traversal tests](../SmartX.Api.Tests/MeshTraversalTests.cs) | Cycles, disconnected nodes and unavailable devices |
| Minimum spanning tree | [MST tests](../SmartX.Api.Tests/MeshMinimumSpanningTreeTests.cs) | Known minimum cost, unavailable nodes, isolated roots and equal-cost ties |
| Routing and availability | [Routing tests](../SmartX.Api.Tests/MeshRoutingTests.cs), [service tests](../SmartX.Api.Tests/MeshTopologyServiceTests.cs) | Path costs, unreachable results, heartbeat expiry and recovery |
| HTTP integration | [API tests](../SmartX.Api.Tests/TelemetryApiTests.cs) | History and topology endpoint behaviour |

The [README screenshots](../README.md#screenshots) show the initial route, the offline alternative, history filters and the passing test suite. Screenshots demonstrate visible outcomes; the linked source and assertions provide implementation evidence.

Run the existing suite from the repository root:

```powershell
dotnet test SmartX.slnx
```

## 9. References

Microsoft. 2026a. *Dictionary&lt;TKey,TValue&gt; Class*. [Online]. Available at: [Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0) [Accessed 12 October 2026].

Microsoft. 2026b. *PriorityQueue&lt;TElement,TPriority&gt; Class*. [Online]. Available at: [Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.priorityqueue-2?view=net-10.0) [Accessed 12 October 2026].

Sedgewick, R. and Wayne, K. 2026a. *BreadthFirstPaths.java*. [Online]. Available at: [Princeton University](https://algs4.cs.princeton.edu/code/edu/princeton/cs/algs4/BreadthFirstPaths.java.html) [Accessed 12 October 2026].

Sedgewick, R. and Wayne, K. 2026b. *LazyPrimMST.java*. [Online]. Available at: [Princeton University](https://algs4.cs.princeton.edu/code/edu/princeton/cs/algs4/LazyPrimMST.java.html) [Accessed 12 October 2026].

Silva, M. 2026. *AVLTreeST.java*. [Online]. Available at: [Princeton University](https://algs4.cs.princeton.edu/code/edu/princeton/cs/algs4/AVLTreeST.java.html) [Accessed 12 October 2026].

The references support the algorithm explanations. The code examples in this document come from the Smart-X C# implementation. The complete attribution list is maintained in [CODE_ATTRIBUTION.txt](../CODE_ATTRIBUTION.txt).
