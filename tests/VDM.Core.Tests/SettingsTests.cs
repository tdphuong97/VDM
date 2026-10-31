namespace VDM.Core.Tests;

public sealed class SettingsTests
{
    [Theory]
    [InlineData(32, 32)]
    [InlineData(24, 24)]
    [InlineData(64, 32)]
    [InlineData(0, 1)]
    public void Connections_go_up_to_32_like_idm(int asked, int kept)
    {
        var s = new AppSettings { Connections = asked };
        s.Normalize();
        Assert.Equal(kept, s.Connections);
    }
}

public sealed class CaptureAndFolderDefaultsTests
{
    [Fact]
    public void Catching_every_file_type_and_sorting_into_folders_are_on_for_existing_users_too()
    {
        // A settings file written by version 1.0 has neither field.
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"connections": 8}""", JsonStore.Options)!;
        Assert.True(old.CaptureAll);
        Assert.True(old.SortByType);
        Assert.True(old.ShareBandwidth);
    }

    [Fact]
    public void Remembered_folders_survive_an_old_or_damaged_settings_file()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"rememberedFolders": null}""", JsonStore.Options)!;
        old.Normalize();
        Assert.Empty(old.RememberedFolders);

        var damaged = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            """{"rememberedFolders": {"video": "D:\\Phim", "audio": "", "archive": "relative\\x"}}""", JsonStore.Options)!;
        damaged.Normalize();
        Assert.Equal(new Dictionary<string, string> { ["video"] = @"D:\Phim" }, damaged.RememberedFolders);
    }
}

public sealed class IdmFileTypesTests
{
    const string Idm = "3GP 7Z AAC ACE AIF APK ARJ ASF AVI BIN BZ2 EXE GZ GZIP IMG ISO LZH M4A M4V MKV MOV MP3 MP4 MPA MPE MPEG MPG MSI MSU OGG OGV PDF PLJ PPS PPT QT R0* R1* RA RAR RM RMVB SEA SIT SITX TAR TIF TIFF WAV WMA WMV Z ZIP";

    [Fact]
    public void The_default_list_is_idms_list()
    {
        var expected = Idm.ToLowerInvariant().Split(' ').OrderBy(e => e, StringComparer.Ordinal).ToList();
        var s = new AppSettings();
        s.Normalize();
        Assert.Equal(expected, s.CaptureExtensions);
    }

    [Fact]
    public void Wildcards_like_r0_star_survive_saving()
    {
        var s = new AppSettings { CaptureExtensions = new() { "R0*", ".r1*", "zip", "bad ext", "a/b" } };
        s.Normalize();
        Assert.Equal(new[] { "r0*", "r1*", "zip" }, s.CaptureExtensions);
    }

    [Fact]
    public void A_list_saved_by_version_1_0_becomes_idms_list_but_an_edited_one_is_kept()
    {
        var old = new AppSettings { CaptureExtensions = AppSettings.Version10Extensions.ToList() };
        old.Normalize();
        Assert.Contains("r0*", old.CaptureExtensions);

        var edited = new AppSettings { CaptureExtensions = new() { "zip", "pdf" } };
        edited.Normalize();
        Assert.Equal(new[] { "pdf", "zip" }, edited.CaptureExtensions);
    }

    [Theory]
    [InlineData("phim.rmvb", "video")]
    [InlineData("clip.qt", "video")]
    [InlineData("nhac.aif", "audio")]
    [InlineData("bai.ra", "audio")]
    [InlineData("old.arj", "archive")]
    [InlineData("game.part.r07", "archive")]
    [InlineData("game.r12", "archive")]
    [InlineData("scan.tiff", "image")]
    [InlineData("update.msu", "program")]
    [InlineData("slide.pps", "document")]
    public void Idms_types_are_sorted_into_folders(string name, string category) =>
        Assert.Equal(category, FileNames.Category(name));
}
