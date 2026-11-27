using Beesharp.Client.Shared;

namespace Beesharp.Client.Tests;

/// <summary>A call name in a narrow column keeps its method and the end of its path (the part that tells calls apart); only the middle gets cut.</summary>
public class CallNamePartsTests
{
    [Fact]
    public void A_short_name_stays_whole()
    {
        Assert.Equal(("GET /products", ""), Format.CallNameParts("GET /products"));
    }

    [Fact]
    public void A_long_name_keeps_the_last_path_segment_as_its_tail()
    {
        var (head, tail) = Format.CallNameParts("GET /products/12345/reviews");
        Assert.Equal("GET /products/12345", head);
        Assert.Equal("/reviews", tail);
    }

    [Fact]
    public void A_long_last_segment_keeps_its_last_characters()
    {
        var name = "POST /api/orders/0123456789abcdef0123456789abcdef";
        var (head, tail) = Format.CallNameParts(name);
        Assert.Equal(name, head + tail);
        Assert.StartsWith("POST /api", head);
        Assert.EndsWith("cdef", tail);
        Assert.InRange(tail.Length, 6, 18);
    }
}
