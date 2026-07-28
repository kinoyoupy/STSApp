namespace STSApp.Backend.Options;

/// <summary>
/// 音声ファイルの保存先設定です。
/// DBにはファイル本体ではなく、ここで保存したファイルへの参照情報を保存します。
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string AudioRootPath { get; init; } = "storage/audio";
}
