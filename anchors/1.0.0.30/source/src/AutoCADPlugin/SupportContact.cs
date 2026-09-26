namespace AutoCADPlugin;

// The original shared boundary identifies which support an opened end belongs
// to. A later anchorage must not jump to an unrelated support along the ray.
internal sealed class SupportContact
{
    public SupportContact(int supportIndex, ContourGraph.Edge boundary)
    {
        SupportIndex = supportIndex;
        Boundary = boundary;
    }

    public int SupportIndex { get; }
    public ContourGraph.Edge Boundary { get; }
}
