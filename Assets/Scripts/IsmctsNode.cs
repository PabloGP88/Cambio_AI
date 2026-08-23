using System.Collections.Generic;

/* one node in the ISMCTS tree: the action that reached it, its parent, its children keyed
   by action, and the running visit, availability and reward stats the tree policy backs up */
public sealed class Node
{
    public readonly GameCommand Action;
    public readonly Node parent;
    public readonly Dictionary<GameCommand, Node> children = new();
    public readonly int Depth;

    public int visits;
    public int avail;
    public double reward;

    public double AvgReward => visits > 0 ? reward / visits : 0.0;

    public Node(GameCommand action, Node parent)
    {
        Action = action;
        this.parent = parent;
        Depth = parent == null ? 0 : parent.Depth + 1;
    }
}
