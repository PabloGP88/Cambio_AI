using System;
using System.Collections.Generic;

/* ISMCTS tree policy. one search = many determinized iterations, each doing
   selection, expansion, optional rollout, evaluation and backprop on a shared tree.
   determinization itself lives in AICambioAgent_Determinize.cs */
public partial class AICambioAgent
{
    private Node NewRoot()
    {
        _nodesExpandedThisSearch = 0;
        _failedDeterminizations = 0;
        return new Node(default, null);
    }

    private void RunOneIteration(Node root, GameState publicState, int i)
    {
        GameState world = Determinize(publicState, i);
        if (world == null)
        {
            _failedDeterminizations++; 
            return;
        }
        SimulateOnce(world, root, i);
    }

    private void SimulateOnce(GameState world, Node root, int iteration)
    {
        Node node = root;
        var path = new List<Node> { root };

        while (!world.IsTerminal)
        {
            List<GameCommand> legal = world.LegalMoves();
            if (legal.Count == 0) break;

            int side = world.ActiveSide;

            // single pass: bump availability for every legal move that already has a node,
            // meaning it was available this descent, and remember the first untried move
            GameCommand? untried = null;
            foreach (var move in legal)
            {
                if (node.children.TryGetValue(move, out var c)) c.avail++;
                else if (!untried.HasValue) untried = move;
            }

            // expansion
            if (untried.HasValue)
            {
                world.Apply(untried.Value);
                var child = new Node(untried.Value, node);
                _nodesExpandedThisSearch++;
                node.children[untried.Value] = child;
                path.Add(child);
                node = child;

                if (MctsDebug.At(2))
                    MctsDebug.Log(2, $"iter={iteration} EXPAND depth={child.Depth} side={side} action={untried.Value}");
                break;
            }

            // selection
            Node chosen = null;
            double bestUcb = double.NegativeInfinity;
            foreach (var move in legal)
            {
                Node c = node.children[move];
                double u = Ucb(c, side);
                if (u > bestUcb) { bestUcb = u; chosen = c; }
            }

            if (MctsDebug.At(3))
                MctsDebug.Log(3, $"iter={iteration} SELECT depth={node.Depth} side={side} -> {chosen.Action} " +
                                 $"ucb={bestUcb:F3} visits={chosen.visits} avg={chosen.AvgReward:F3}");

            world.Apply(chosen.Action);
            path.Add(chosen);
            node = chosen;
        }

        double reward = Rollout(world, iteration, node.Depth);

        foreach (var n in path)
        {
            n.visits++;
            n.reward += reward;
        }

        if (MctsDebug.At(3))
            MctsDebug.Log(3, $"iter={iteration} BACKPROP reward={reward:F3} across {path.Count} nodes");
    }

    private double Ucb(Node child, int chooser)
    {
        double exploit = child.reward / child.visits;
        if (chooser != _mySide)
        {
            exploit = 1.0 - exploit;   // opponent minimises AI reward
        }
        
        double explore = Exploration * Math.Sqrt(Math.Log(child.avail) / child.visits);
        return exploit + explore;
    }

    private double Rollout(GameState world, int iteration, int startDepth)
    {
        int plies = 0;
        while (!world.IsTerminal && plies < RolloutPlyCap)
        {
            List<GameCommand> legal = world.LegalMoves();
            if (legal.Count == 0) break;
            world.Apply(legal[_rng.Next(legal.Count)]);
            plies++;
        }

        double result = Evaluate(world);

        if (MctsDebug.At(3))
            MctsDebug.Log(3, $"iter={iteration} ROLLOUT from depth={startDepth} ran {plies} plies, " +
                             $"terminal={world.IsTerminal} -> reward={result:F3}");
        return result;
    }

    private const double EvalTempo = 8.0;         // softness of the tanh
    private const double EvalTargetScore = 14.0;  // AI hand score we treat as ok
    private const double PenaltyAversion = 0.3;

    private double Evaluate(GameState world)
    {
        if (world.IsTerminal)
        {
            int w = world.WinnerSide();
            if (w == _mySide) return 1.0;
            if (w < 0) return 0.5;
            return 0.0;
        }

        int ai  = world.Score(_mySide);
        int opp = world.Score(GameState.OpponentOf(_mySide));
        double rel = 0.5 + 0.5 * Math.Tanh((opp - ai) / EvalTempo);
        double abs = 0.5 - 0.5 * Math.Tanh((ai - EvalTargetScore) / EvalTempo);

        // linear, un-saturated cost for penalty cards the AI is carrying
        double aiPenalty = 0;
        foreach (var s in world.GetActiveSlots(_mySide))
            if (s.Zone == Zone.Penalty) aiPenalty += world.GetCard(s).Value;

        double blended = 0.5 * rel + 0.5 * abs - PenaltyAversion * aiPenalty;
        return blended < 0 ? 0 : blended > 1 ? 1 : blended;
    }

    private GameCommand MostVisited(Node root, List<GameCommand> legalAtRoot)
    {
        Node best = null;
        foreach (var move in legalAtRoot)
            if (root.children.TryGetValue(move, out var child))
                if (best == null || child.visits > best.visits) best = child;

        if (best == null)
        {
            MctsDebug.LogWarning($"MostVisited: no expanded children out of {legalAtRoot.Count} legal moves " +
                                 $"({_failedDeterminizations} determinizations skipped) — picking randomly.");
            return legalAtRoot[_rng.Next(legalAtRoot.Count)];
        }
        return best.Action;
    }
}
