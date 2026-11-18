using System.IO.Compression;
using System.Text;
using GTAModManager.Core.Peds;

namespace GTAModManager.Core.Tests;

public sealed class PedArchiveTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "gmm-ped-archive-" + Guid.NewGuid().ToString("N"));

    public PedArchiveTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, true);

    static byte[] Resource() => [82, 83, 67, 55, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void Unregistered_mixed_layout_is_ambiguous_and_does_not_guess_a_variant_preview()
    {
        var a = PedArchiveAnalyzer.Analyze(
            Zip(
                new()
                {
                    ["hero.yft"] = Resource(),
                    ["hero.ymt"] = Resource(),
                    ["hero.ydd"] = Resource(),
                    ["hero/head.ydd"] = Resource(),
                    ["Unrelated/photo.jpg"] = [1],
                }
            )
        );
        var c = Assert.Single(a.Candidates);
        Assert.True(c.LayoutAmbiguous);
        Assert.Null(c.PreviewPath);
    }

    [Fact]
    public void Variant_previews_and_all_general_images_are_retained_separately()
    {
        var a = PedArchiveAnalyzer.Analyze(
            Zip(
                new()
                {
                    ["SKIN 1/Va.yft"] = Resource(),
                    ["SKIN 1/Va.ymt"] = Resource(),
                    ["SKIN 1/Va.ydd"] = Resource(),
                    ["SKIN 2/Vb.yft"] = Resource(),
                    ["SKIN 2/Vb.ymt"] = Resource(),
                    ["SKIN 2/Vb.ydd"] = Resource(),
                    ["SKIN 1/PREVIEW.jpg"] = [1],
                    ["SKIN 2/PREVIEW.jpg"] = [2],
                    ["WARDROBE.jpg"] = [3],
                    ["WARNING.jpg"] = [4],
                }
            )
        );
        Assert.Equal(4, a.ImagePaths.Count);
        Assert.Equal(@"SKIN 1\PREVIEW.jpg", a.Candidates[0].PreviewPath);
        Assert.Equal(@"SKIN 2\PREVIEW.jpg", a.Candidates[1].PreviewPath);
    }

    string Zip(Dictionary<string, byte[]> files)
    {
        var p = Path.Combine(root, Guid.NewGuid() + ".zip");
        using var z = ZipFile.Open(p, ZipArchiveMode.Create);
        foreach (var (n, b) in files)
        {
            using var s = z.CreateEntry(n).Open();
            s.Write(b);
        }
        return p;
    }

    [Fact]
    public void Coherent_models_variants_and_preview_are_found_without_treating_folders_as_models()
    {
        var a = PedArchiveAnalyzer.Analyze(
            Zip(
                new()
                {
                    ["A/hero.yft"] = Resource(),
                    ["A/hero.ymt"] = Resource(),
                    ["A/hero.ydd"] = Resource(),
                    ["B/hero.yft"] = Resource(),
                    ["B/hero.ymt"] = Resource(),
                    ["B/hero.ydd"] = Resource(),
                    ["B/hero.ytd"] = Resource(),
                    ["Grace.yft"] = Resource(),
                    ["Grace.ymt"] = Resource(),
                    ["Grace/head_000_u.ydd"] = Resource(),
                    ["Grace/head_diff_000_a_uni.ytd"] = Resource(),
                    ["Grace/Variant/unused.jpg"] = [1],
                    ["readme.txt"] = Encoding.UTF8.GetBytes("Описание вариантов"),
                    ["preview.jpg"] = [1],
                }
            )
        );
        Assert.Equal(3, a.Candidates.Count);
        var variants = a.Candidates.Where(c => c.SpawnName == "hero").ToArray();
        Assert.Equal(2, variants.Length);
        Assert.NotEqual(variants[0].Id, variants[1].Id);
        Assert.All(variants, c => Assert.Equal(PedLayout.Normal, c.Layout));
        var streamed = Assert.Single(a.Candidates, c => c.SpawnName == "Grace");
        Assert.Equal(PedLayout.Streamed, streamed.Layout);
        Assert.Contains(streamed.Resources, r => r.TargetPath == @"Grace\head_000_u.ydd");
        Assert.Contains("Описание", a.Readme);
        Assert.Contains("preview.jpg", a.ImagePaths);
    }

    [Fact]
    public void Full_matching_registration_is_retained_and_bad_headers_warn()
    {
        var xml =
            "<CPedModelInfo__InitDataList><InitDatas><Item custom='yes'><Name>hero</Name><Pedtype>COP</Pedtype><IsStreamedGfx value='false'/><Unknown a='b'>custom</Unknown></Item></InitDatas></CPedModelInfo__InitDataList>";
        var a = PedArchiveAnalyzer.Analyze(
            Zip(
                new()
                {
                    ["hero.yft"] = Resource(),
                    ["hero.ymt"] = [0],
                    ["hero.ydd"] = Resource(),
                    ["peds.meta"] = Encoding.UTF8.GetBytes(xml),
                    ["AddonPedsVars.xml"] = Encoding.UTF8.GetBytes(
                        "<AddonPeds_Vars><Personality><Item>Streamed_Male</Item></Personality></AddonPeds_Vars>"
                    ),
                }
            )
        );
        var c = Assert.Single(a.Candidates);
        Assert.Contains("Unknown", c.RegistrationXml);
        Assert.Contains("custom=\"yes\"", c.RegistrationXml);
        Assert.NotEmpty(c.Warnings);
    }

    [Fact]
    public void Incomplete_and_unsafe_paths_are_not_candidates()
    {
        var a = PedArchiveAnalyzer.Analyze(
            Zip(
                new()
                {
                    ["hero.yft"] = Resource(),
                    ["../evil.ymt"] = Resource(),
                    ["folder/notes.txt"] = [1],
                }
            )
        );
        Assert.Empty(a.Candidates);
        Assert.NotEmpty(a.Warnings);
    }

    [Fact]
    public void Nested_variant_uses_its_own_metadata_instead_of_first_same_name_record()
    {
        byte[] Meta(string marker) =>
            Encoding.UTF8.GetBytes(
                $"<CPedModelInfo__InitDataList><InitDatas><Item><Name>hero</Name><Marker>{marker}</Marker></Item></InitDatas></CPedModelInfo__InitDataList>"
            );
        var a = PedArchiveAnalyzer.Analyze(
            Zip(
                new()
                {
                    ["A/models/hero.yft"] = Resource(),
                    ["A/models/hero.ymt"] = Resource(),
                    ["A/models/hero.ydd"] = Resource(),
                    ["A/peds.meta"] = Meta("first"),
                    ["B/models/hero.yft"] = Resource(),
                    ["B/models/hero.ymt"] = Resource(),
                    ["B/models/hero.ydd"] = Resource(),
                    ["B/peds.meta"] = Meta("second"),
                }
            )
        );
        Assert.Contains(
            "<Marker>second</Marker>",
            Assert.Single(a.Candidates, c => c.Id.StartsWith("B\\")).RegistrationXml
        );
    }

    [Fact]
    public void Streamed_alternative_subfolders_are_warned_and_not_merged_into_model()
    {
        var a = PedArchiveAnalyzer.Analyze(
            Zip(
                new()
                {
                    ["hero.yft"] = Resource(),
                    ["hero.ymt"] = Resource(),
                    ["hero/head_000_u.ydd"] = Resource(),
                    ["hero/Alternative/head_000_u.ydd"] = Resource(),
                }
            )
        );
        var c = Assert.Single(a.Candidates);
        Assert.DoesNotContain(c.Resources, r => r.TargetPath.Contains("Alternative"));
        Assert.NotEmpty(c.Warnings);
    }

    [Fact]
    public void Conventional_props_resources_are_part_of_the_same_model()
    {
        var a = PedArchiveAnalyzer.Analyze(
            Zip(
                new()
                {
                    ["hero.yft"] = Resource(),
                    ["hero.ymt"] = Resource(),
                    ["hero.ydd"] = Resource(),
                    ["hero_p.ydd"] = Resource(),
                    ["hero_p/prop_000.ytd"] = Resource(),
                }
            )
        );
        var c = Assert.Single(a.Candidates);
        Assert.Contains(c.Resources, r => r.TargetPath == "hero_p.ydd");
        Assert.Contains(c.Resources, r => r.TargetPath == @"hero_p\prop_000.ytd");
    }
}

public sealed class PedDefaultRegistrationTests
{
    [Theory]
    [InlineData(GTAModManager.Core.Peds.PedGender.Female, "CIVFEMALE")]
    [InlineData(GTAModManager.Core.Peds.PedGender.Male, "CIVMALE")]
    public void Default_registration_is_complete_like_working_reference(
        GTAModManager.Core.Peds.PedGender gender,
        string pedtype
    )
    {
        var item = GTAModManager.Core.Peds.PedMetadata.ParseItem(
            GTAModManager.Core.Peds.PedMetadata.CreateDefault(
                "TestPed",
                gender,
                GTAModManager.Core.Peds.PedLayout.Normal
            )
        );
        Xunit.Assert.Equal(70, item.Elements().Count());
        Xunit.Assert.Equal(pedtype, item.Element("Pedtype")!.Value);
        // The "Female" pose matcher does not exist in the game: human models use "Male" for both genders.
        Xunit.Assert.Equal("Male", item.Element("PoseMatcherName")!.Value);
        Xunit.Assert.Equal("3.000000", item.Element("HDDist")!.Attribute("value")!.Value);
        Xunit.Assert.Equal("BAR_PERSON_PVG", item.Element("PedVoiceGroup")!.Value);
    }
}
