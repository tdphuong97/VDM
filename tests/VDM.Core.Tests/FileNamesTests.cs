using System.Net.Http.Headers;

namespace VDM.Core.Tests;

public sealed class FileNamesTests
{
    [Theory]
    [InlineData("a:b*c?.zip", "a_b_c_.zip")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("  name.  ", "name")]
    [InlineData("", "download")]
    public void Sanitize_makes_names_windows_safe(string input, string expected) =>
        Assert.Equal(expected, FileNames.Sanitize(input));

    [Fact]
    public void Plain_filename_sent_as_raw_utf8_is_decoded()
    {
        var raw = System.Text.Encoding.Latin1.GetString(System.Text.Encoding.UTF8.GetBytes("Phim hay tập 1.mp4"));
        var cd = ContentDispositionHeaderValue.Parse("attachment; filename=\"" + raw + "\"");
        Assert.Equal("Phim hay tập 1.mp4", FileNames.FromContentDisposition(cd));
    }

    [Fact]
    public void Name_comes_from_the_url_and_gets_an_extension_from_the_mime_type()
    {
        var name = FileNames.Choose(null, null, new Uri("https://example.org/get/setup"), "application/x-msdownload");
        Assert.Equal("setup.exe", name);
    }

    [Fact]
    public void MakeUnique_skips_names_on_disk_and_names_already_claimed()
    {
        var dir = Directory.CreateTempSubdirectory("vdm-names-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.zip"), "");
            File.WriteAllText(Path.Combine(dir, "a (1).zip.vdmpart"), "");
            var taken = new HashSet<string> { "a (2).zip" };
            Assert.Equal("a (3).zip", FileNames.MakeUnique(dir, "a.zip", taken));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("movie.MKV", "video")]
    [InlineData("song.mp3", "audio")]
    [InlineData("setup.exe", "program")]
    [InlineData("backup.7z", "archive")]
    [InlineData("noext", "other")]
    public void Category_groups_by_extension(string name, string category) =>
        Assert.Equal(category, FileNames.Category(name));
}
