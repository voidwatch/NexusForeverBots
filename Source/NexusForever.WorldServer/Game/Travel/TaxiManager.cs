using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Travel
{
    public enum TaxiResult
    {
        Ok,
        UnknownNode,
        NotUnlocked,
        NoRoute,
        NotAtNode,
        CannotAfford,
        WrongFaction
    }

    /// <summary>
    /// Taxi travel between TaxiNode rows along TaxiRoute rows. The flight itself is not simulated: the player pays the route price and is
    /// teleported to the destination node's WorldLocation2 (docs/ASSUMPTIONS.md "Taxis"). Nodes unlock at AutoUnlockLevel or when visited.
    /// </summary>
    public class TaxiManager
    {
        private const float NodeRange = 120f;

        private readonly Player player;
        private readonly HashSet<uint> visited = new();

        public TaxiManager(Player player)
        {
            this.player = player;
        }

        public bool IsUnlocked(uint nodeId)
        {
            TaxiNodeEntry node = GameTableManager.Instance.TaxiNode.GetEntry(nodeId);
            return node != null && (visited.Contains(nodeId) || (node.AutoUnlockLevel > 0u && player.Level >= node.AutoUnlockLevel));
        }

        public void Unlock(uint nodeId)
        {
            if (GameTableManager.Instance.TaxiNode.GetEntry(nodeId) != null)
                visited.Add(nodeId);
        }

        public static bool TryGetLocation(uint nodeId, out ushort world, out Vector3 position)
        {
            world = 0;
            position = default;
            TaxiNodeEntry node = GameTableManager.Instance.TaxiNode.GetEntry(nodeId);
            WorldLocation2Entry location = node == null ? null : GameTableManager.Instance.WorldLocation2.GetEntry(node.WorldLocation2Id);
            if (location == null)
                return false;

            world = (ushort)location.WorldId;
            position = new Vector3(location.Position0, location.Position1, location.Position2);
            return true;
        }

        /// <summary>The node the player stands at: same world, within range of the node's location.</summary>
        public uint? CurrentNode()
        {
            if (player.Map == null)
                return null;

            foreach (TaxiNodeEntry node in GameTableManager.Instance.TaxiNode.Entries.Where(n => n != null))
                if (TryGetLocation(node.Id, out ushort world, out Vector3 position) && world == player.Map.Entry.Id && Vector3.Distance(position, player.Position) <= NodeRange)
                    return node.Id;

            return null;
        }

        public bool FactionAllowed(uint nodeId)
        {
            TaxiNodeEntry node = GameTableManager.Instance.TaxiNode.GetEntry(nodeId);
            if (node == null || node.TaxiNodeFactionEnum == 0u)
                return true;

            return node.TaxiNodeFactionEnum == 1u ? player.Faction1 == Reputation.Static.Faction.Exile : player.Faction1 == Reputation.Static.Faction.Dominion;
        }

        public TaxiResult Travel(uint destinationNode)
        {
            TaxiResult result = TryTravel(destinationNode);
            FailureLedger.Record(result == TaxiResult.Ok ? "taxi-travel-ok" : "taxi-travel-refused", result.ToString(), $"destination {destinationNode}");
            return result;
        }

        private TaxiResult TryTravel(uint destinationNode)
        {
            if (!TryGetLocation(destinationNode, out ushort world, out Vector3 position))
                return TaxiResult.UnknownNode;

            if (!IsUnlocked(destinationNode))
                return TaxiResult.NotUnlocked;

            // TaxiNodeFactionEnum: 1 = Exile towns (Thayd, Gallow), 2 = Dominion camps (Camp Virtue), 0 = both (names checked against the node texts)
            if (!FactionAllowed(destinationNode))
                return TaxiResult.WrongFaction;

            uint? source = CurrentNode();
            if (source == null)
                return TaxiResult.NotAtNode;

            if (!FactionAllowed(source.Value))
                return TaxiResult.WrongFaction;

            TaxiRouteEntry route = GameTableManager.Instance.TaxiRoute.Entries
                .FirstOrDefault(r => r != null && r.TaxiNodeIdSource == source && r.TaxiNodeIdDestination == destinationNode);
            if (route == null)
                return TaxiResult.NoRoute;

            if (!player.CurrencyManager.CanAfford(CurrencyType.Credits, route.Price))
                return TaxiResult.CannotAfford;

            player.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, route.Price);
            player.TeleportTo(world, position.X, position.Y, position.Z);
            Unlock(destinationNode);
            return TaxiResult.Ok;
        }

        /// <summary>
        /// Client 0x00FF (PurchaseFlightPath / TakeShuttle): a path of taxi nodes from the node the player stands at. Every hop needs a TaxiRoute
        /// row; the price is the sum of the hop prices; the player arrives at the last node (the flight itself is not simulated). The path may
        /// start with the current node or with the first hop (the client sends its route list, docs/ASSUMPTIONS.md "Taxis").
        /// </summary>
        public TaxiResult TravelPath(IReadOnlyList<uint> nodes)
        {
            TaxiResult result = TryTravelPath(nodes, out uint price);
            FailureLedger.Record(result == TaxiResult.Ok ? "taxi-path-ok" : "taxi-path-refused", result.ToString(), $"{nodes.Count} nodes, {price} credits");
            return result;
        }

        private TaxiResult TryTravelPath(IReadOnlyList<uint> nodes, out uint price)
        {
            price = 0u;
            if (nodes.Count == 0)
                return TaxiResult.UnknownNode;

            uint? source = CurrentNode();
            if (source == null)
                return TaxiResult.NotAtNode;
            if (!FactionAllowed(source.Value))
                return TaxiResult.WrongFaction;

            uint previous = source.Value;
            foreach (uint node in nodes.SkipWhile(n => n == source.Value))
            {
                if (GameTableManager.Instance.TaxiNode.GetEntry(node) == null)
                    return TaxiResult.UnknownNode;
                if (!FactionAllowed(node))
                    return TaxiResult.WrongFaction;

                TaxiRouteEntry route = GameTableManager.Instance.TaxiRoute.Entries
                    .FirstOrDefault(r => r != null && r.TaxiNodeIdSource == previous && r.TaxiNodeIdDestination == node);
                if (route == null)
                    return TaxiResult.NoRoute;

                price += route.Price;
                previous = node;
            }

            if (previous == source.Value)
                return TaxiResult.NoRoute;
            if (!IsUnlocked(previous))
                return TaxiResult.NotUnlocked;
            if (!TryGetLocation(previous, out ushort world, out Vector3 position))
                return TaxiResult.UnknownNode;
            if (!player.CurrencyManager.CanAfford(CurrencyType.Credits, price))
                return TaxiResult.CannotAfford;

            player.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, price);
            player.TeleportTo(world, position.X, position.Y, position.Z);
            Unlock(previous);
            return TaxiResult.Ok;
        }

        /// <summary>Shortest hop path (breadth first over TaxiRoute) from one node to another, node ids after the source; null when unreachable.</summary>
        public static List<uint> FindPath(uint from, uint to)
        {
            var previous = new Dictionary<uint, uint> { [from] = from };
            var queue = new Queue<uint>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                uint node = queue.Dequeue();
                if (node == to)
                    break;
                foreach (TaxiRouteEntry route in GameTableManager.Instance.TaxiRoute.Entries.Where(r => r != null && r.TaxiNodeIdSource == node))
                    if (previous.TryAdd(route.TaxiNodeIdDestination, node))
                        queue.Enqueue(route.TaxiNodeIdDestination);
            }

            if (!previous.ContainsKey(to) || from == to)
                return null;
            var path = new List<uint>();
            for (uint n = to; n != from; n = previous[n])
                path.Insert(0, n);
            return path;
        }
    }
}
