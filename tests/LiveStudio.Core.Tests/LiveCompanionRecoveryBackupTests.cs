using System.Text.Json;
using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Contracts;
using LiveStudio.Core;

namespace LiveStudio.Core.Tests;

public sealed class LiveCompanionRecoveryBackupTests
{
    [Fact]
    public async Task DiskScanKeepsEmptyCameraContainerWithoutCollectingUnrelatedSceneData()
    {
        var root = Path.Combine(Path.GetTempPath(), "LiveStudio-empty-camera-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "WBStore"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "WBStore", "sourceStore.json"),
                """{"sourceStore":{"sceneSource":{"scene":{"data":{},"data2":{"text":{"type":"text","payload":{"text":"private-scene-content"}}},"layout":"excluded"}}}}""");
            var document = Assert.Single(await new LiveCompanionConfigurationStore(root).CaptureDocumentsAsync(CancellationToken.None));
            var value = Assert.Single(document.Values);
            Assert.Equal("/sourceStore/sceneSource/scene/data", value.JsonPointer);
            Assert.Equal(JsonValueKind.Object, value.Value.ValueKind);
            Assert.Empty(value.Value.EnumerateObject());
            Assert.DoesNotContain("private-scene-content", JsonSerializer.Serialize(document), StringComparison.Ordinal);
            Assert.Null(LiveCompanionPortableProfile.TryCreate([document]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingCameraCanBeArchivedWithoutBecomingARestorableTarget()
    {
        NativeConfigurationDocument[] documents =
        [
            Document("sourceStore", "/sourceStore/sceneSource/scene/data", new { }),
            Document("effectConfigStore", "/effectConfigStore/configs", new { }),
            Document("effectStore", "/effectStore/useCurveFilter", true),
            Document("filterStore", "/filterStore/items", Array.Empty<string>())
        ];
        Assert.Null(LiveCompanionPortableProfile.TryCreate(documents));

        var backup = await LiveCompanionAdapter.CreateReadOnlyRecoveryBackupAsync(
            "12.9.2.470033184", false, documents, CancellationToken.None);

        Assert.Equal(documents, backup.NativeDocuments);
        Assert.Empty(backup.Sources);
        Assert.Equal(CompatibilityLevel.Unsupported, backup.Compatibility);
        Assert.NotEmpty(backup.FieldCoverage);
        Assert.All(backup.FieldCoverage, field =>
        {
            Assert.False(field.Writable);
            Assert.Equal(FieldEvidenceStatus.EvidenceOnly, field.EvidenceStatus);
        });
        var adapter = new LiveCompanionAdapter(new LiveCompanionAdapterCatalog());
        Assert.Same(backup, adapter.PrepareRestoreSnapshot(backup));
        var context = new RestoreExecutionContext(Guid.NewGuid(), backup, [], false, Path.GetTempPath());
        var preflight = await adapter.PreflightAsync(context, CancellationToken.None);
        Assert.False(preflight.CanProceed);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.BeginRestoreAsync(context, CancellationToken.None));
    }

    private static NativeConfigurationDocument Document<T>(string name, string pointer, T value) => new(
        "webcast_mate", "JsonFile", "json-v1", $"WBStore/{name}.json", $"WBStore/{name}.json",
        new string('a', 64), Guid.NewGuid(),
        [new NativeConfigurationValue(pointer, NativeParameterCategories.Filter, JsonSerializer.SerializeToElement(value))]);
}
