using System.Text.Json;
using LiveStudio.Contracts;
using LiveStudio.Desktop.ViewModels;

namespace LiveStudio.Core.Tests;

public sealed class SnapshotInspectorCoverageTests
{
    [Fact]
    public void LiveCompanionCurvesAreExpandedIntoEveryControlPointField()
    {
        var application = CreateLiveCompanionApplication(
            CompatibilityLevel.Verified,
            [
                new NativeConfigurationValue(
                    "/filters/curves",
                    "Filter",
                    JsonSerializer.SerializeToElement(new
                    {
                        master = new[]
                        {
                            new { x = 0.0, y = 0.0 },
                            new { x = 0.5, y = 0.62 },
                            new { x = 1.0, y = 1.0 }
                        },
                        red = new[]
                        {
                            new { x = 0.0, y = 0.04 },
                            new { x = 1.0, y = 0.96 }
                        }
                    }))
            ],
            [
                new CapturedParameterField(
                    "studio.json:/filters/curves",
                    "FilterSetting",
                    "Object",
                    true,
                    true,
                    "SignedAdapterReadback",
                    EvidenceStatus: FieldEvidenceStatus.Mapped)
            ]);

        var viewModel = new SnapshotApplicationViewModel(application);

        Assert.Equal(10, viewModel.CoverageFields.Count);
        Assert.All(viewModel.CoverageFields, field => Assert.False(field.IsGap));
        Assert.Contains(
            viewModel.CoverageFields,
            field => field.TechnicalPath == "studio.json:/filters/curves/master/1/y"
                     && field.Value == "0.62"
                     && field.Name == "y");
        Assert.Equal("已映射 10 项，尚未完成真机验收", viewModel.CoverageStatus);
    }

    [Fact]
    public void DeclaredFieldWithoutCapturedValueIsReportedAsGap()
    {
        var application = CreateLiveCompanionApplication(
            CompatibilityLevel.Verified,
            [],
            [
                new CapturedParameterField(
                    "studio.json:/filters/curves",
                    "FilterSetting",
                    "Object",
                    false,
                    true,
                    "SignedAdapterReadback",
                    EvidenceStatus: FieldEvidenceStatus.Mapped)
            ]);

        var viewModel = new SnapshotApplicationViewModel(application);

        var gap = Assert.Single(viewModel.CoverageFields);
        Assert.True(gap.IsGap);
        Assert.Equal("没有捕获到值", gap.Value);
        Assert.Equal("1 项缺失或未声明", viewModel.CoverageStatus);
    }

    [Fact]
    public void DiscoveryDataIsVisibleButNeverPresentedAsRestorableCoverage()
    {
        var application = CreateLiveCompanionApplication(
            CompatibilityLevel.Unsupported,
            [
                new NativeConfigurationValue(
                    "/filters/curve",
                    "Filter",
                    JsonSerializer.SerializeToElement(new { x = 0.2, y = 0.3 }))
            ],
            [
                new CapturedParameterField(
                    "studio.json:/filters/curve",
                    "Filter",
                    "Object",
                    true,
                    false,
                    "DiscoveryReadOnly",
                    EvidenceStatus: FieldEvidenceStatus.EvidenceOnly)
            ]);

        var viewModel = new SnapshotApplicationViewModel(application);

        Assert.Equal(2, viewModel.CoverageFields.Count);
        Assert.All(viewModel.CoverageFields, field =>
        {
            Assert.True(field.IsGap);
            Assert.Equal("仅有证据", field.Status);
            Assert.Equal("探测读取（不可恢复）", field.Verification);
        });
    }

    private static ApplicationSnapshot CreateLiveCompanionApplication(
        CompatibilityLevel compatibility,
        IReadOnlyList<NativeConfigurationValue> values,
        IReadOnlyList<CapturedParameterField> coverage)
    {
        var document = new NativeConfigurationDocument(
            "main",
            "JsonFile",
            "live-companion-test",
            "studio.json",
            "studio.json",
            new string('a', 64),
            Guid.Parse("0da1999a-d69c-4f6c-ad6b-5c0367e0c104"),
            values);
        return new ApplicationSnapshot(
            ApplicationKind.LiveCompanion,
            "test",
            "live-companion-test",
            new string('b', 64),
            new string('c', 64),
            compatibility,
            true,
            coverage,
            [],
            [document]);
    }
}
