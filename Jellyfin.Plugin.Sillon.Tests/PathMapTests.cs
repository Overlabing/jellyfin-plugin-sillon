using Jellyfin.Plugin.Sillon.Requests;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public class PathMapTests
{
    [Theory]
    [InlineData("/data/media/music/Vald/XEU (2022)", "/data/music/Vald/XEU (2022)")]
    [InlineData("/data/media/music", "/data/music")]
    [InlineData("/data/media/musical/Vald", "/data/media/musical/Vald")]
    [InlineData("/elsewhere/Vald", "/elsewhere/Vald")]
    public void Remap_ReplacesOnlyTheLidarrPrefix(string input, string expected)
        => Assert.Equal(expected, PathMap.Remap(input, "/data/media/music", "/data/music"));

    [Fact]
    public void Remap_ToleratesTrailingSlashesInThePrefixes()
        => Assert.Equal("/data/music/Vald", PathMap.Remap("/data/media/music/Vald", "/data/media/music/", "/data/music/"));

    [Fact]
    public void Remap_WithoutAPrefix_LeavesThePathAlone()
        => Assert.Equal("/a/b", PathMap.Remap("/a/b", string.Empty, "/x"));

    [Theory]
    [InlineData("/a//b/./c/", "/a/b/c")]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("/../a", "/a")]
    [InlineData("a/../..", "..")]
    [InlineData("", ".")]
    public void Clean_MatchesPosixClean(string input, string expected)
        => Assert.Equal(expected, PathMap.Clean(input));

    [Theory]
    [InlineData("/data/media/music/Vald/XEU (2022)/01 - Intro.flac", "/data/media/music/Vald/XEU (2022)")]
    [InlineData("/track.flac", "/")]
    [InlineData("track.flac", ".")]
    public void Dir_IsTheDirectoryOfAFile(string input, string expected)
        => Assert.Equal(expected, PathMap.Dir(input));
}
