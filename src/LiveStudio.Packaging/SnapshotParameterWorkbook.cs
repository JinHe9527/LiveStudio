using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LiveStudio.Contracts;

namespace LiveStudio.Packaging;

// 产品运行时直接生成标准 OOXML，不依赖本机安装 Excel 或开发环境的表格工具。
public static partial class SnapshotParameterWorkbook
{
    public const string PackagePath = "parameters.xlsx";
    public const string MediaType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteBesidePackageAsync(string packagePath, CancellationToken cancellationToken)
    {
        var inspection = await SnapshotPackageReader.InspectAsync(packagePath, cancellationToken);
        var destination = Path.ChangeExtension(Path.GetFullPath(packagePath), ".xlsx");
        var temporary = $"{destination}.partial-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(temporary, Create(inspection.Package.Snapshot), cancellationToken);
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static byte[] Create(CombinedSnapshot snapshot)
    {
        var parameters = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        var findings = SensitiveDataScanner.ScanJson(parameters, "parameters.json");
        if (findings.Count > 0)
            throw new SnapshotSensitiveDataException(string.Join(Environment.NewLine, findings));

        var sheets = new List<Sheet>();
        var overview = new Sheet("恢复指南", "完整参数 · 手动恢复工作簿", ["项目", "内容", "说明"], [26, 65, 75]);
        overview.Rows.Add(["存档名称", snapshot.Name, "内容来自此存档，修改 Excel 不会改变存档或自动写入软件"]);
        overview.Rows.Add(["保存时间", snapshot.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), "保留保存时区"]);
        overview.Rows.Add(["应用数量", snapshot.Applications.Count, "OBS / 抖音直播伴侣"]);
        overview.Rows.Add(["素材数量", snapshot.Assets.Count, "按内容哈希去重，详见素材清单"]);
        overview.Rows.Add(["素材大小（字节）", snapshot.Assets.Sum(asset => asset.Length), "原始文件大小；存档压缩后大小不同"]);
        overview.Rows.Add(["手动恢复顺序", "应用 → 视频来源 → 设备模式 → 滤镜顺序 → 参数 → 素材", "先备份当前配置，再逐项核对；绿色仅表示手工标记完成"]);
        overview.Rows.Add(["核对操作", "参数页最后一列选择：待核对 / 已核对 / 无法设置", "冻结表头；使用筛选查找参数或未完成项"]);
        overview.Rows.Add(["完整性", "参数页用于阅读，完整字段页保留所有已存数据", "空值、空对象、空数组、数组顺序和长文本分段均保留；不代表未捕获功能已覆盖"]);
        overview.Rows.Add(["滤镜依赖", "当前存档保存滤镜参数及已识别素材", "第三方插件程序本体尚未封装；目标软件仍须具备相应滤镜实现"]);
        overview.Rows.Add(["素材取出", ".lscfg 是 ZIP 存档；按素材清单包内路径提取", "保存为原文件名后，在对应参数选择该文件；不要修改原存档"]);
        overview.Rows.Add(["硬件备注", "相机机身备注保留在完整字段页", "仅供人工参考，软件不能自动还原物理拍摄环境"]);
        sheets.Add(overview);

        foreach (var app in snapshot.Applications)
        {
            var name = app.Kind == ApplicationKind.Obs ? "OBS 参数" : "直播伴侣参数";
            var sheet = new Sheet(name, $"{name} · {app.Version}",
                ["分组 / 来源", "参数名称", "保存值", "值类型", "覆盖状态", "原生位置 / 顺序", "手动核对"], [30, 32, 48, 16, 20, 55, 18], true);
            foreach (var source in app.Sources)
            {
                sheet.Rows.Add([source.Name, "设备名称", source.Device?.FriendlyName ?? "未记录", "文本", "已保存", "device/friendlyName", "待核对"]);
                if (source.Mode is { } mode)
                {
                    sheet.Rows.Add([source.Name, "分辨率（宽 × 高）", $"{mode.Width} × {mode.Height}", "像素", "已保存", "mode/width · mode/height", "待核对"]);
                    sheet.Rows.Add([source.Name, "帧率 FPS（分子 / 分母）", $"{mode.FramesPerSecondNumerator} / {mode.FramesPerSecondDenominator}", "帧/秒", "已保存", "mode/framesPerSecond", "待核对"]);
                    sheet.Rows.Add([source.Name, "像素格式", mode.PixelFormat, "文本", "已保存", "mode/pixelFormat", "待核对"]);
                    sheet.Rows.Add([source.Name, "色彩空间", mode.ColorSpace, "文本", "已保存", "mode/colorSpace", "待核对"]);
                    sheet.Rows.Add([source.Name, "色彩范围", mode.ColorRange, "文本", "已保存", "mode/colorRange", "待核对"]);
                }
                else sheet.Rows.Add([source.Name, "视频模式", "未记录", "空值", "待确认", "mode", "待核对"]);
                foreach (var setting in source.Settings)
                    AddValue(sheet, source.Name, setting.Key, setting.Value, "已保存", "settings/" + setting.Key);
                foreach (var filter in source.Filters.OrderBy(filter => filter.Order))
                {
                    var group = $"{source.Name} / {filter.Name}";
                    sheet.Rows.Add([group, "滤镜类型 / 顺序 / 开关", $"{filter.Kind} / {filter.Order} / {(filter.Enabled ? "开启" : "关闭")}", "滤镜", "已保存", "filters", "待核对"]);
                    foreach (var setting in filter.Settings)
                        AddValue(sheet, group, setting.Key, setting.Value, "已保存", "settings/" + setting.Key);
                }
            }
            foreach (var section in app.ConfigurationTree?.Sections ?? []) AddSection(sheet, section);
            sheets.Add(sheet);
        }

        var assets = new Sheet("素材清单", "素材与文件位置", ["文件名", "大小（字节）", "原电脑路径", "参数引用", "包内路径", "SHA-256"], [30, 20, 55, 45, 55, 70]);
        foreach (var binding in SnapshotAssetBindings.Collect(snapshot.Applications))
        {
            assets.Rows.Add([binding.OriginalFileName, binding.Length, binding.SourcePath, binding.ReferencePath,
                snapshot.Assets.FirstOrDefault(asset => asset.Sha256 == binding.BlobSha256)?.PackagePath ?? "缺失：未找到素材内容", binding.BlobSha256]);
        }
        sheets.Add(assets);
        var complete = new Sheet("完整字段", "全部已存字段 · 精确值与结构", ["字段路径", "类型", "分段", "精确保存值"], [85, 18, 16, 85]);
        foreach (var leaf in Flatten(JsonSerializer.SerializeToElement(snapshot, JsonOptions), ""))
        {
            var value = leaf.Value.ValueKind == JsonValueKind.String ? leaf.Value.GetString()! : leaf.Value.GetRawText();
            // Excel 单元格最多 32767 字符；按 UTF-16 安全边界分段，不截断任何保存值。
            var chunks = Split(value).ToArray();
            for (var index = 0; index < chunks.Length; index++)
                complete.Rows.Add([leaf.Path, leaf.Value.ValueKind.ToString(), $"{index + 1}/{chunks.Length}", chunks[index]]);
        }
        sheets.Add(complete);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            XNamespace content = "http://schemas.openxmlformats.org/package/2006/content-types";
            Write(archive, "[Content_Types].xml", new XElement(content + "Types",
                new XElement(content + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(content + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                Override("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
                Override("/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"),
                sheets.Select((_, i) => Override($"/xl/worksheets/sheet{i + 1}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))));
            Write(archive, "_rels/.rels", Rels([Rel("rId1", "officeDocument", "xl/workbook.xml")]));
            Write(archive, "xl/workbook.xml", new XElement(Main + "workbook", new XAttribute(XNamespace.Xmlns + "r", Relationships),
                new XElement(Main + "sheets", sheets.Select((sheet, i) => new XElement(Main + "sheet", new XAttribute("name", sheet.Name), new XAttribute("sheetId", i + 1), new XAttribute(Relationships + "id", $"rId{i + 1}"))))));
            Write(archive, "xl/_rels/workbook.xml.rels", Rels(sheets.Select((_, i) => Rel($"rId{i + 1}", "worksheet", $"worksheets/sheet{i + 1}.xml")).Append(Rel("styles", "styles", "styles.xml"))));
            Write(archive, "xl/styles.xml", XElement.Parse(Styles));
            for (var i = 0; i < sheets.Count; i++) Write(archive, $"xl/worksheets/sheet{i + 1}.xml", Worksheet(sheets[i]));
            XElement Override(string path, string type) => new(content + "Override", new XAttribute("PartName", path), new XAttribute("ContentType", type));
        }
        return stream.ToArray();
    }

    private static void AddSection(Sheet sheet, ConfigurationSectionSnapshot section)
    {
        foreach (var field in section.Fields.OrderBy(field => field.Order))
            AddValue(sheet, field.UiPath, field.NativeName, field.CurrentValue, field.EvidenceStatus switch
            {
                FieldEvidenceStatus.Verified => "已验证",
                FieldEvidenceStatus.Mapped => "已映射",
                FieldEvidenceStatus.EvidenceOnly => "仅采集证据",
                _ => "覆盖未确认"
            }, field.Locator.NativePath);
        foreach (var child in section.Sections.OrderBy(child => child.Order)) AddSection(sheet, child);
    }

    private static void AddValue(Sheet sheet, string group, string name, JsonElement value, string status, string path)
    {
        foreach (var leaf in Flatten(value, ""))
        {
            var raw = leaf.Value.ValueKind == JsonValueKind.String ? leaf.Value.GetString()! : leaf.Value.GetRawText();
            var parts = Split(raw).ToArray();
            for (var index = 0; index < parts.Length; index++)
                sheet.Rows.Add([group, name + leaf.Path + (parts.Length > 1 ? $"（分段 {index + 1}/{parts.Length}）" : ""), parts[index], leaf.Value.ValueKind.ToString(), status, path + leaf.Path, "待核对"]);
        }
    }

    private static IEnumerable<(string Path, JsonElement Value)> Flatten(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any())
        {
            foreach (var property in value.EnumerateObject())
                foreach (var leaf in Flatten(property.Value, path + "/" + property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal))) yield return leaf;
        }
        else if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                foreach (var leaf in Flatten(item, path + "/" + (index++).ToString(CultureInfo.InvariantCulture))) yield return leaf;
        }
        else yield return (path, value);
    }

    private static IEnumerable<string> Split(string value)
    {
        if (value.Length == 0) { yield return ""; yield break; }
        for (var offset = 0; offset < value.Length;)
        {
            var length = Math.Min(300, value.Length - offset);
            if (offset + length < value.Length && char.IsHighSurrogate(value[offset + length - 1])) length--;
            yield return value.Substring(offset, length);
            offset += length;
        }
    }

    private sealed record Sheet(string Name, string Title, string[] Headers, double[] Widths, bool Checklist = false)
    {
        public List<object[]> Rows { get; } = [];
    }

    private static XElement Worksheet(Sheet sheet)
    {
        if (sheet.Rows.Count > 1_048_572) throw new SnapshotPackageException("参数数量超过单工作表容量，未生成截断的 Excel");
        var last = ((char)('A' + sheet.Headers.Length - 1)).ToString();
        var end = sheet.Rows.Count + 4;
        var rows = new List<XElement> { Row(2, [sheet.Title], 1, sheet.Widths), Row(4, sheet.Headers, 2, sheet.Widths) };
        rows.AddRange(sheet.Rows.Select((values, i) => Row(i + 5, values, i % 2 == 0 ? 3 : 4, sheet.Widths)));
        return new XElement(Main + "worksheet",
            new XElement(Main + "sheetViews", new XElement(Main + "sheetView", new XAttribute("workbookViewId", 0), new XAttribute("showGridLines", 0),
                new XElement(Main + "pane", new XAttribute("ySplit", 4), new XAttribute("topLeftCell", "A5"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
            new XElement(Main + "cols", sheet.Widths.Select((width, i) => new XElement(Main + "col", new XAttribute("min", i + 1), new XAttribute("max", i + 1), new XAttribute("width", width), new XAttribute("customWidth", 1)))),
            new XElement(Main + "sheetData", rows),
            new XElement(Main + "autoFilter", new XAttribute("ref", $"A4:{last}{Math.Max(4, end)}")),
            new XElement(Main + "mergeCells", new XAttribute("count", 1), new XElement(Main + "mergeCell", new XAttribute("ref", $"A2:{last}2"))),
            sheet.Checklist && end >= 5 ? new XElement(Main + "conditionalFormatting", new XAttribute("sqref", $"G5:G{end}"),
                new XElement(Main + "cfRule", new XAttribute("type", "cellIs"), new XAttribute("dxfId", 0), new XAttribute("priority", 1), new XAttribute("operator", "equal"), new XElement(Main + "formula", "\"已核对\"")),
                new XElement(Main + "cfRule", new XAttribute("type", "cellIs"), new XAttribute("dxfId", 1), new XAttribute("priority", 2), new XAttribute("operator", "equal"), new XElement(Main + "formula", "\"无法设置\""))) : null,
            sheet.Checklist && end >= 5 ? new XElement(Main + "dataValidations", new XAttribute("count", 1), new XElement(Main + "dataValidation", new XAttribute("type", "list"), new XAttribute("allowBlank", 1), new XAttribute("sqref", $"G5:G{end}"), new XElement(Main + "formula1", "\"待核对,已核对,无法设置\""))) : null);
    }

    private static XElement Row(int number, object[] values, int style, double[] widths) => new(Main + "row", new XAttribute("r", number), new XAttribute("ht", number == 2 ? 32 : number == 4 ? 28 : Math.Min(409, Math.Max(34, values.Select((value, index) =>
        (value.ToString() ?? "").Split('\n').Sum(line => Math.Max(1, Math.Ceiling(line.Sum(character => character > 127 ? 2d : 1d) / Math.Max(10, widths[index] - 4)))))
        .Max() * 17 + 12))), new XAttribute("customHeight", 1),
        values.Select((value, i) => new XElement(Main + "c", new XAttribute("r", $"{(char)('A' + i)}{number}"), new XAttribute("s", style),
            new XAttribute("t", value is int or long ? "n" : "inlineStr"), value is int or long
                ? new XElement(Main + "v", Convert.ToString(value, CultureInfo.InvariantCulture))
                : new XElement(Main + "is", new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), ExcelText(value.ToString() ?? ""))))));

    private static string ExcelText(string value)
    {
        var escaped = ExcelEscapePattern().Replace(value, "_x005F_$1");
        return string.Concat(escaped.Select(character => character < 32 && character is not '\t' and not '\n' and not '\r'
            ? $"_x{(int)character:X4}_" : character.ToString()));
    }
    [GeneratedRegex("_(x[0-9A-Fa-f]{4}_)")]
    private static partial Regex ExcelEscapePattern();
    private static XElement Rel(string id, string type, string target) => new(XName.Get("Relationship", "http://schemas.openxmlformats.org/package/2006/relationships"), new XAttribute("Id", id), new XAttribute("Type", Relationships.NamespaceName + "/" + type), new XAttribute("Target", target));
    private static XElement Rels(IEnumerable<XElement> values) => new(XName.Get("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships"), values);
    private static void Write(ZipArchive archive, string path, XElement element)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        element.Save(writer, SaveOptions.DisableFormatting);
    }

    private const string Styles = """
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="3"><font><sz val="11"/><color rgb="FF253247"/><name val="Microsoft YaHei"/></font><font><b/><sz val="17"/><color rgb="FF17263C"/><name val="Microsoft YaHei"/></font><font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Microsoft YaHei"/></font></fonts>
          <fills count="4"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF243B53"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFF1F5F9"/><bgColor indexed="64"/></patternFill></fill></fills>
          <borders count="1"><border/></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="5"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="center"/></xf><xf numFmtId="0" fontId="2" fillId="2" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="center"/></xf><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf><xf numFmtId="0" fontId="0" fillId="3" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf></cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
          <dxfs count="2"><dxf><fill><patternFill patternType="solid"><fgColor rgb="FFDDF3E4"/></patternFill></fill></dxf><dxf><fill><patternFill patternType="solid"><fgColor rgb="FFFCE1DE"/></patternFill></fill></dxf></dxfs>
        </styleSheet>
        """;
}
