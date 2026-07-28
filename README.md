# STSApp 実行手順

Backend + Avalonia Desktop の音声対話アプリです。

初期開発では、実STT/TTS/Gemini APIを設定しなくても動作確認できるように、開発用モックを使います。
開発用モックでは、実際の文字起こしやAI応答生成は行わず、固定の文字起こし結果、固定のAI返答、短い無音WAVを返します。

## 1. 前提

- .NET SDK
- Docker Desktop
- macOS

現在の録音と音声再生はmacOS向けです。

## 2. MySQLを起動する

プロジェクトルートで次を実行します。

```bash
docker compose up -d
```

MySQLは次の設定で起動します。

- Database: `sts_app`
- User: `sts_user`
- Password: `sts_password`
- Port: `3306`

Backendの開発用設定も、このMySQLへ接続する前提です。

## 3. Backend設定を確認する

Backendのローカル設定ファイルは次です。

```text
STSApp.Backend/appsettings.Development.json
```

実APIなしで確認する場合は、`ExternalApis` 直下が次のようになっていることを確認します。

```json
{
  "ExternalApis": {
    "UseDevelopmentMocks": true
  }
}
```

`UseDevelopmentMocks` が `true` の間は、STT/TTS/Geminiの実URLやAPIキーが空でも動作確認できます。

## 4. Backendを起動する

別ターミナルで次を実行します。

```bash
dotnet run --project STSApp.Backend/STSApp.Backend.csproj
```

起動URLは次です。

```text
http://localhost:5133
```

起動時にDevelopment環境の場合、BackendがMySQLのテーブルを自動作成します。

DB接続確認をする場合は、Backend起動後に次へアクセスします。

```text
http://localhost:5133/api/health/database
```

`{"status":"ok"}` が返れば、BackendからMySQLへ接続できています。

## 5. Avalonia Desktopを起動する

さらに別ターミナルで次を実行します。

```bash
AVALONIA_TELEMETRY_OPTOUT=1 dotnet run --project STSApp.Desktop/STSApp.Desktop.csproj
```

Desktop側のBackend接続先は次のファイルで管理しています。

```text
STSApp.Desktop/appsettings.json
```

現在の既定値は次です。

```json
{
  "backendBaseUrl": "http://127.0.0.1:5133"
}
```

## 6. 実APIなしで動作確認する

1. MySQLを起動する
2. Backendを起動する
3. Avalonia Desktopを起動する
4. PushToTalkボタンを押して録音開始
5. ボタンを離して録音終了
6. チャットUIに開発用の文字起こし結果とAI返答が表示されることを確認する
7. 短い無音WAVが生成され、音声取得・再生処理まで進むことを確認する

録音した入力音声は次へ保存されます。

```text
STSApp.Backend/storage/audio/input/YYYYMMDD
```

開発用TTSが生成した返答音声は次へ保存されます。

```text
STSApp.Backend/storage/audio/output/YYYYMMDD
```

音声ファイルそのものはDBに保存せず、DBには保存先の参照情報だけを保存します。

## 7. 実APIを使う時

実STT/TTS/Gemini APIの疎通確認へ進む時は、次のファイルを編集します。

```text
STSApp.Backend/appsettings.Development.json
```

`UseDevelopmentMocks` を `false` に戻します。

```json
{
  "ExternalApis": {
    "UseDevelopmentMocks": false
  }
}
```

そのうえで、STT/TTSのBaseUrl、GeminiのAPIキー、Geminiのモデル名などをローカル設定へ入れます。

実URLやAPIキーはREADMEや設計書には書かず、ローカルの `appsettings.Development.json` だけで管理します。

## 8. ビルド確認

Backend:

```bash
dotnet build STSApp.Backend/STSApp.Backend.csproj --no-restore
```

Desktop:

```bash
AVALONIA_TELEMETRY_OPTOUT=1 dotnet build STSApp.Desktop/STSApp.Desktop.csproj --no-restore
```

Avaloniaのビルド時にローカルログの権限エラーが出ることがあるため、Desktopの確認では `AVALONIA_TELEMETRY_OPTOUT=1` を付けています。

## 9. よく見るファイル

- `STSsys_Document.md`: 設計書
- `DEVELOPMENT_STATUS.md`: 開発状況メモ
- `STSApp.Backend/appsettings.Development.json`: Backendのローカル設定
- `STSApp.Desktop/appsettings.json`: DesktopのBackend接続先設定
- `docker-compose.yml`: MySQL開発環境
