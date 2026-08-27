using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveStudio.Contracts;
using LiveStudio.Desktop.Services;
using LiveStudio.Packaging;

namespace LiveStudio.Desktop.ViewModels;

public sealed record CameraCreativeLookOptionViewModel(string Code, string Name)
{
    public string Display => $"{Code} · {Name}";
}

public partial class CameraStationEditorViewModel : ObservableObject
{
    private readonly string defaultName;
    private readonly IReadOnlyList<CameraCreativeLookOptionViewModel> creativeLooks;
    private CameraReferenceImageSnapshot? referenceImageMetadata;
    private string? pendingReferenceImagePath;
    private string? pendingReferenceImageSha256;
    private bool removeReferenceImage;

    public CameraStationEditorViewModel(
        int slot,
        string defaultName,
        IReadOnlyList<CameraCreativeLookOptionViewModel> creativeLooks)
    {
        Slot = slot;
        this.defaultName = defaultName;
        this.creativeLooks = creativeLooks;
        Reset();
    }

    public int Slot { get; }

    public string PositionTitle => $"{Slot + 1} 号机位";

    public IReadOnlyList<CameraCreativeLookOptionViewModel> CreativeLooks => creativeLooks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteButtonText))]
    public partial Guid? ProfileId { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Aperture { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ShutterSpeed { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Iso { get; set; } = string.Empty;

    [ObservableProperty]
    public partial CameraCreativeLookOptionViewModel? SelectedCreativeLook { get; set; }

    [ObservableProperty]
    public partial string Contrast { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Highlights { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Shadows { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Fade { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Saturation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Sharpness { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SharpnessRange { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Clarity { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "尚未保存";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReferenceImage))]
    [NotifyPropertyChangedFor(nameof(IsReferenceImageEmpty))]
    [NotifyPropertyChangedFor(nameof(CanRemoveReferenceImage))]
    public partial Bitmap? ReferenceImage { get; set; }

    [ObservableProperty]
    public partial string ReferenceImageStatus { get; set; } = "拖入画面截图，或点击选择";

    public bool HasReferenceImage => ReferenceImage is not null;

    public bool IsReferenceImageEmpty => ReferenceImage is null;

    public bool CanRemoveReferenceImage => ReferenceImage is not null;

    public string ReferenceImageTitle => $"{Name}参考画面";

    public CameraReferenceImageSnapshot? ReferenceImageMetadata => referenceImageMetadata;

    public string DeleteButtonText => ProfileId.HasValue ? "删除" : "清空";

    public void Apply(CameraProfile profile)
    {
        ProfileId = profile.Id;
        Name = profile.Name;
        Aperture = profile.Aperture;
        ShutterSpeed = profile.ShutterSpeed;
        Iso = profile.Iso;
        SelectedCreativeLook = creativeLooks.FirstOrDefault(
            option => string.Equals(option.Code, profile.CreativeLook, StringComparison.OrdinalIgnoreCase))
            ?? creativeLooks[0];
        var settings = profile.EffectiveCreativeLookSettings;
        Contrast = settings.Contrast.ToString(CultureInfo.InvariantCulture);
        Highlights = settings.Highlights.ToString(CultureInfo.InvariantCulture);
        Shadows = settings.Shadows.ToString(CultureInfo.InvariantCulture);
        Fade = settings.Fade.ToString(CultureInfo.InvariantCulture);
        Saturation = settings.Saturation.ToString(CultureInfo.InvariantCulture);
        Sharpness = settings.Sharpness.ToString(CultureInfo.InvariantCulture);
        SharpnessRange = settings.SharpnessRange.ToString(CultureInfo.InvariantCulture);
        Clarity = settings.Clarity.ToString(CultureInfo.InvariantCulture);
        StatusText = $"已保存 · {profile.UpdatedAt.ToLocalTime():MM-dd HH:mm}";
    }

    public void Apply(CameraStationSnapshot station)
    {
        ProfileId = null;
        Name = station.Name;
        Aperture = station.Aperture;
        ShutterSpeed = station.ShutterSpeed;
        Iso = station.Iso;
        SelectedCreativeLook = creativeLooks.FirstOrDefault(
            option => string.Equals(option.Code, station.CreativeLook, StringComparison.OrdinalIgnoreCase))
            ?? creativeLooks[0];
        Contrast = station.CreativeLookSettings.Contrast.ToString(CultureInfo.InvariantCulture);
        Highlights = station.CreativeLookSettings.Highlights.ToString(CultureInfo.InvariantCulture);
        Shadows = station.CreativeLookSettings.Shadows.ToString(CultureInfo.InvariantCulture);
        Fade = station.CreativeLookSettings.Fade.ToString(CultureInfo.InvariantCulture);
        Saturation = station.CreativeLookSettings.Saturation.ToString(CultureInfo.InvariantCulture);
        Sharpness = station.CreativeLookSettings.Sharpness.ToString(CultureInfo.InvariantCulture);
        SharpnessRange = station.CreativeLookSettings.SharpnessRange.ToString(CultureInfo.InvariantCulture);
        Clarity = station.CreativeLookSettings.Clarity.ToString(CultureInfo.InvariantCulture);
        StatusText = "随当前画面存档保存";
        ReferenceImage = null;
        referenceImageMetadata = station.ReferenceImage;
        pendingReferenceImagePath = null;
        pendingReferenceImageSha256 = null;
        removeReferenceImage = false;
        ReferenceImageStatus = station.ReferenceImage is null
            ? "拖入画面截图，或点击选择"
            : "正在读取存档图片…";
    }

    public void Reset()
    {
        ProfileId = null;
        Name = defaultName;
        Aperture = "F4";
        ShutterSpeed = "1/125";
        Iso = "640";
        SelectedCreativeLook = creativeLooks[0];
        var settings = CameraCreativeLookSettings.StandardDefault;
        Contrast = settings.Contrast.ToString(CultureInfo.InvariantCulture);
        Highlights = settings.Highlights.ToString(CultureInfo.InvariantCulture);
        Shadows = settings.Shadows.ToString(CultureInfo.InvariantCulture);
        Fade = settings.Fade.ToString(CultureInfo.InvariantCulture);
        Saturation = settings.Saturation.ToString(CultureInfo.InvariantCulture);
        Sharpness = settings.Sharpness.ToString(CultureInfo.InvariantCulture);
        SharpnessRange = settings.SharpnessRange.ToString(CultureInfo.InvariantCulture);
        Clarity = settings.Clarity.ToString(CultureInfo.InvariantCulture);
        StatusText = "尚未保存";
        ReferenceImage = null;
        referenceImageMetadata = null;
        pendingReferenceImagePath = null;
        pendingReferenceImageSha256 = null;
        removeReferenceImage = false;
        ReferenceImageStatus = "拖入画面截图，或点击选择";
    }

    public void StageReferenceImage(string sourcePath, ValidatedCameraReferenceImage image)
    {
        using var stream = new MemoryStream(image.Content.ToArray(), writable: false);
        var bitmap = new Bitmap(stream);
        ReferenceImage = bitmap;
        referenceImageMetadata = image.CreateSnapshot(Slot);
        pendingReferenceImagePath = Path.GetFullPath(sourcePath);
        pendingReferenceImageSha256 = image.Sha256;
        removeReferenceImage = false;
        ReferenceImageStatus = $"{image.PixelWidth}×{image.PixelHeight} · 等待保存";
    }

    public void ApplyReferenceImageContent(ReadOnlyMemory<byte> content)
    {
        if (referenceImageMetadata is not { } metadata)
        {
            return;
        }

        var validated = CameraReferenceImageFile.Validate(content);
        if (!string.Equals(validated.Sha256, metadata.Sha256, StringComparison.Ordinal)
            || validated.PixelWidth != metadata.PixelWidth
            || validated.PixelHeight != metadata.PixelHeight)
        {
            throw new InvalidDataException($"{PositionTitle}参考图与存档记录不一致");
        }

        using var stream = new MemoryStream(content.ToArray(), writable: false);
        ReferenceImage = new Bitmap(stream);
        pendingReferenceImagePath = null;
        pendingReferenceImageSha256 = null;
        removeReferenceImage = false;
        ReferenceImageStatus = $"{metadata.PixelWidth}×{metadata.PixelHeight} · 已随存档保存";
    }

    public void RemoveReferenceImage()
    {
        var hadSavedImage = referenceImageMetadata is not null;
        ReferenceImage = null;
        referenceImageMetadata = null;
        pendingReferenceImagePath = null;
        pendingReferenceImageSha256 = null;
        removeReferenceImage = hadSavedImage;
        ReferenceImageStatus = hadSavedImage ? "删除将在保存后生效" : "拖入画面截图，或点击选择";
    }

    public CameraReferenceImageChange? CreateReferenceImageChange()
    {
        if (removeReferenceImage)
        {
            return new CameraReferenceImageChange(Slot, null, null, true);
        }

        return pendingReferenceImagePath is null || pendingReferenceImageSha256 is null
            ? null
            : new CameraReferenceImageChange(
                Slot,
                pendingReferenceImagePath,
                pendingReferenceImageSha256,
                false);
    }

    partial void OnReferenceImageChanging(Bitmap? oldValue, Bitmap? newValue)
    {
        if (!ReferenceEquals(oldValue, newValue))
        {
            oldValue?.Dispose();
        }
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(ReferenceImageTitle));

    public bool TryGetCreativeLookSettings(out CameraCreativeLookSettings settings, out string error)
    {
        settings = CameraCreativeLookSettings.StandardDefault;
        error = string.Empty;
        parseError = string.Empty;
        if (!TryParse(Contrast, -9, 9, "对比度", out var contrast)
            || !TryParse(Highlights, -9, 9, "高光", out var highlights)
            || !TryParse(Shadows, -9, 9, "阴影", out var shadows)
            || !TryParse(Fade, 0, 9, "褪色", out var fade)
            || !TryParse(Saturation, -9, 9, "饱和度", out var saturation)
            || !TryParse(Sharpness, 0, 9, "锐度", out var sharpness)
            || !TryParse(SharpnessRange, 0, 5, "锐度范围", out var sharpnessRange)
            || !TryParse(Clarity, 0, 9, "清晰度", out var clarity))
        {
            error = parseError;
            return false;
        }

        settings = new CameraCreativeLookSettings(
            contrast,
            highlights,
            shadows,
            fade,
            saturation,
            sharpness,
            sharpnessRange,
            clarity);
        return true;

        bool TryParse(string text, int minimum, int maximum, string label, out int value)
        {
            if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                && value >= minimum
                && value <= maximum)
            {
                return true;
            }

            parseError = $"{label}请输入 {minimum}–{maximum} 的整数";
            return false;
        }
    }

    private string parseError = string.Empty;

    public bool TryCreateSnapshot(out CameraStationSnapshot snapshot, out string error)
    {
        snapshot = default!;
        if (!TryGetCreativeLookSettings(out var settings, out error))
        {
            return false;
        }

        if (!CameraProfileInput.TryNormalize(
                Name,
                Aperture,
                ShutterSpeed,
                Iso,
                SelectedCreativeLook?.Code ?? string.Empty,
                settings,
                out var input,
                out error))
        {
            return false;
        }

        snapshot = new CameraStationSnapshot(
            Slot,
            input.Name,
            input.Aperture,
            input.ShutterSpeed,
            input.Iso,
            input.CreativeLook,
            new CameraCreativeLookSnapshot(
                input.CreativeLookSettings.Contrast,
                input.CreativeLookSettings.Highlights,
                input.CreativeLookSettings.Shadows,
                input.CreativeLookSettings.Fade,
                input.CreativeLookSettings.Saturation,
                input.CreativeLookSettings.Sharpness,
                input.CreativeLookSettings.SharpnessRange,
                input.CreativeLookSettings.Clarity),
            referenceImageMetadata);
        return true;
    }
}

public sealed class CameraProfileItemViewModel(CameraProfile profile)
{
    public CameraProfile Profile { get; } = profile;

    public Guid Id => Profile.Id;

    public string Name => Profile.Name;

    public string UpdatedAt => Profile.UpdatedAt.ToLocalTime().ToString(
        "M月d日 HH:mm",
        CultureInfo.CurrentCulture);

    public string ExposureSummary =>
        $"{Profile.Aperture} · {Profile.ShutterSpeed} · ISO {Profile.Iso} · {Profile.CreativeLook}";

    public string CreativeLookSummary
    {
        get
        {
            var settings = Profile.EffectiveCreativeLookSettings;
            return $"对比 {settings.Contrast:+0;-0;0} · 高光 {settings.Highlights:+0;-0;0} · "
                + $"阴影 {settings.Shadows:+0;-0;0} · 褪色 {settings.Fade} · "
                + $"饱和 {settings.Saturation:+0;-0;0} · 锐度 {settings.Sharpness} · "
                + $"范围 {settings.SharpnessRange} · 清晰 {settings.Clarity}";
        }
    }

    public string ModeText => Profile.Mode == CameraProfileMode.Wired ? "插线保存" : "手动记录";
}

public static partial class CameraProfileInput
{
    private static readonly HashSet<string> CreativeLooks = new(
        ["ST", "PT", "NT", "VV", "VV2", "FL", "IN", "SH", "BW", "SE"],
        StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^(?:F)?(?<value>[0-9]+(?:\\.[0-9]+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex AperturePattern();

    [GeneratedRegex("^(?:1/(?<denominator>[1-9][0-9]{0,5})|(?<seconds>[0-9]+(?:\\.[0-9]+)?)S?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShutterPattern();

    public static bool TryNormalize(
        string name,
        string aperture,
        string shutterSpeed,
        string iso,
        string creativeLook,
        out CameraProfileInputValues values,
        out string error) => TryNormalize(
            name,
            aperture,
            shutterSpeed,
            iso,
            creativeLook,
            CameraCreativeLookSettings.StandardDefault,
            out values,
            out error);

    public static bool TryNormalize(
        string name,
        string aperture,
        string shutterSpeed,
        string iso,
        string creativeLook,
        CameraCreativeLookSettings creativeLookSettings,
        out CameraProfileInputValues values,
        out string error)
    {
        values = default;
        error = string.Empty;
        var normalizedName = name.Trim();
        if (normalizedName.Length is < 1 or > 80)
        {
            error = "档案名称需要 1–80 个字符";
            return false;
        }

        var apertureMatch = AperturePattern().Match(aperture.Trim().Replace(" ", string.Empty, StringComparison.Ordinal));
        if (!apertureMatch.Success
            || !decimal.TryParse(apertureMatch.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var apertureValue)
            || apertureValue is < 0.7m or > 64m)
        {
            error = "光圈请输入 F0.7–F64，例如 F2.8";
            return false;
        }

        var normalizedShutter = shutterSpeed.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (!ShutterPattern().IsMatch(normalizedShutter))
        {
            error = "快门请输入 1/50 或 2S 这样的格式";
            return false;
        }

        var normalizedIso = iso.Trim().ToUpperInvariant().Replace("ISO", string.Empty, StringComparison.Ordinal).Trim();
        if (!string.Equals(normalizedIso, "AUTO", StringComparison.Ordinal)
            && (!int.TryParse(normalizedIso, NumberStyles.None, CultureInfo.InvariantCulture, out var isoValue)
                || isoValue is < 25 or > 409600))
        {
            error = "ISO 请输入 AUTO 或 25–409600 的整数";
            return false;
        }

        var normalizedLook = creativeLook.Trim().ToUpperInvariant();
        if (!CreativeLooks.Contains(normalizedLook))
        {
            error = "请选择索尼相机支持的创意外观";
            return false;
        }

        if (!creativeLookSettings.IsValid)
        {
            error = "创意外观参数超出相机支持范围";
            return false;
        }

        values = new CameraProfileInputValues(
            normalizedName,
            $"F{apertureValue:0.##}",
            normalizedShutter.EndsWith('S') ? normalizedShutter : normalizedShutter,
            normalizedIso,
            normalizedLook,
            creativeLookSettings);
        return true;
    }
}

public readonly record struct CameraProfileInputValues(
    string Name,
    string Aperture,
    string ShutterSpeed,
    string Iso,
    string CreativeLook,
    CameraCreativeLookSettings CreativeLookSettings);
