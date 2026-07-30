# STSApp 実行手順

Avalonia Desktopで音声を受け取り、ASP.NET Core Backendから
`STT → Vector RAG → Gemini → TTS`を順番に実行する学習用の音声対話アプリです。

音声入力は、ボタンを押している間だけ録音する方式ではありません。
音声入力を開始すると待機を続け、WebRTC VADが発話開始と発話終了を検知した時に、
検知した1発話をBackendへ自動送信します。

## 1. 前提

- .NET SDK 10.0.301
- Docker Desktop
- macOS

使用する.NET SDKは、プロジェクトルートの`global.json`で固定しています。
録音と音声再生のネイティブ処理は、現在macOS向けに実装しています。

## 2. MySQLを起動する

プロジェクトルートで次を実行します。

```bash
docker compose up -d
```

初期値は次のとおりです。

- Database: `sts_app`
- User: `sts_user`
- Password: `sts_password`
- Port: `3306`

これらはローカル開発用の初期値です。必要な場合は`.env`で上書きします。
`.env`はGitの管理対象外です。

## 3. Backendのローカル設定を作る

設定例をコピーして、ローカル専用の設定ファイルを作ります。

```bash
cp STSApp.Backend/appsettings.Development.example.json \
   STSApp.Backend/appsettings.Development.json
```

編集するファイルは次です。

```text
STSApp.Backend/appsettings.Development.json
```

このファイルには、MySQLの接続情報、STT/TTSのURL、Gemini APIキーなどを設定します。
実URLやAPIキーをGitへ送らないため、このファイルはGitの管理対象外です。

### 実APIを使う設定

Vector RAGまで含めて確認する場合は、次を設定します。

```json
{
  "ExternalApis": {
    "UseDevelopmentMocks": false
  }
}
```

あわせて、次の値をローカル設定へ入力します。

- `ConnectionStrings:MySql`
- `ExternalApis:Stt:BaseUrl`
- `ExternalApis:Tts:BaseUrl`
- `ExternalApis:Gemini:ApiKey`
- `ExternalApis:Gemini:ModelName`

RAGのEmbeddingには、同じGemini APIキーを使用します。
Embeddingモデルは既定で`gemini-embedding-001`、出力は768次元です。

### 開発用モックの範囲

`UseDevelopmentMocks=true`では、STT、Gemini返答生成、TTSを固定結果で確認できます。
ただし、RAG検索は検索結果の正しさを確かめる必要があるためモック化していません。

そのため、現在は`UseDevelopmentMocks=true`のまま音声会話を最後まで成功させることはできません。
RAG段階で明確な設定エラーになるのが期待する動作です。

## 4. DBスキーマを準備する

空の開発DBでは、Backendの初回起動時に現在のテーブル一式が作成されます。

以前から使っている開発DBには、必要に応じて手動SQLを番号順に適用します。

```text
STSApp.Backend/Data/Migrations/001_remove_audio_duration_ms.sql
STSApp.Backend/Data/Migrations/002_add_vector_rag.sql
```

`002_add_vector_rag.sql`は、RAG用テーブル、回答根拠、RAG処理段階を追加します。
すでに適用済みのSQLを再実行すると、列やテーブルの重複エラーになるため再実行しません。

開発データを残す必要がなければ、DockerのMySQLボリュームを作り直し、
Backendに現在のスキーマを新規作成させる方法もあります。

## 5. Backendを起動する

```bash
dotnet run --project STSApp.Backend/STSApp.Backend.csproj
```

既定の起動先は次です。

```text
http://localhost:5133
```

DB接続を確認する場合は、Backend起動後に次へアクセスします。

```text
http://localhost:5133/api/health/database
```

`{"status":"ok"}`が返れば、BackendからMySQLへ接続できています。

## 6. RAG資料を取り込む

取り込み対象は`Document/RagKnowledgeBase`にあるVoiceLinkの架空資料です。
`README.md`を除く5資料を、見出し単位の26チャンクへ分割します。

BackendをDevelopment環境で起動し、次を1回実行します。

```bash
curl -X POST http://localhost:5133/api/development/rag/reindex
```

初回は、5資料と26チャンクが取り込まれることを確認します。
資料を変更せずにもう一度実行した場合は、5資料が変更なしとしてスキップされます。

途中でEmbedding APIが失敗した場合は、資料の一部分だけをDBへ反映しません。
全チャンクのEmbeddingが成功した後に、変更内容をまとめて反映するためです。

このAPIは開発環境限定で、認証を追加せずに運用環境へ公開しない前提です。

## 7. Avalonia Desktopを起動する

別のターミナルで次を実行します。

```bash
AVALONIA_TELEMETRY_OPTOUT=1 \
dotnet run --project STSApp.Desktop/STSApp.Desktop.csproj
```

DesktopからBackendへの接続先は、次のファイルで管理します。

```text
STSApp.Desktop/appsettings.json
```

既定値は次です。

```json
{
  "backendBaseUrl": "http://127.0.0.1:5133"
}
```

## 8. 音声会話を確認する

1. `音声入力開始`を押します。
2. 画面が`音声入力待機中`になったことを確認します。
3. マイクへ話しかけます。
4. WebRTC VADが発話と終話を検知すると、音声が自動送信されます。
5. STTの文字起こしがユーザーカードへ表示されます。
6. RAG検索後、Geminiの返答がアシスタントカードへ表示されます。
7. TTS音声が自動再生されます。
8. 再生後、音声入力待機へ戻ることを確認します。

音声入力を終える時は、`音声入力停止`を押します。

VoiceLink資料を根拠にした回答は通常のアシスタントカードとして表示します。
近い資料がなく一般知識で答えた場合だけ、一般的な回答であることをカード内へ表示します。
資料名や類似度は画面へ表示せず、参照履歴をBackendとMySQLへ保存します。

## 9. 音声ファイルの保存場所

録音した入力音声:

```text
STSApp.Backend/storage/audio/input/YYYYMMDD
```

TTSが生成した返答音声:

```text
STSApp.Backend/storage/audio/output/YYYYMMDD
```

音声ファイルそのものはDBに保存しません。
DBには、どの音声ファイルを参照するかという保存先情報を記録します。

## 10. ビルドとテスト

プロジェクト全体:

```bash
dotnet build STSApp.slnx --no-restore
```

Backendの自動テスト:

```bash
dotnet test STSApp.Backend.Tests/STSApp.Backend.Tests.csproj --no-build
```

Avaloniaのビルド時にローカルログの権限エラーが出る場合は、
`AVALONIA_TELEMETRY_OPTOUT=1`を付けて実行します。

## 11. 主なファイル

- `STSsys_Document.md`: システム設計書
- `DEVELOPMENT_STATUS.md`: 開発状況メモ
- `Document/RagKnowledgeBase`: VoiceLinkの架空資料
- `STSApp.Backend/appsettings.Development.example.json`: Backend設定例
- `STSApp.Backend/appsettings.Development.json`: Gitへ送らないローカル設定
- `STSApp.Desktop/appsettings.json`: DesktopのBackend接続先
- `docker-compose.yml`: 開発用MySQL
- `global.json`: 使用する.NET SDK
