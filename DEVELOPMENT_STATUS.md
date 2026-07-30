# STSApp 開発状況メモ

このファイルは、開発を一時停止した後に再開しやすくするための実装状況メモです。

## 2026-07-21 作業記録

### 録音できなかった問題への対応

以前は、PushToTalkを押している間は「録音中」と表示されていましたが、
ボタンを離すと録音データが空になり、WAVファイルが作成されませんでした。

調査した結果、録音開始の画面処理までは進んでいましたが、OpenALから実際のマイク音声を取得できていない状態でした。

対応として、録音処理をOpenALからmacOS標準のAVFoundationを使う方式へ変更しました。

- macOS標準の音声入力機能を使うように変更
- 取得した音声を16kHz、モノラル、16bit PCMへ変換
- 既存の処理と同じWAV形式に変換
- Backendへ送信する処理は変更せず、そのまま利用
- 追加の.NET外部パッケージは使用していない

確認の結果、次の一連の処理が成功しました。

```text
マイク録音
→ WAV生成
→ Backend送信
→ STT
→ Gemini
→ TTS
→ 返答音声再生
```

画面には、ユーザー発話とアシスタント返答のカードが表示されました。
また、当時のPushToTalk方式では、返答音声の再生完了後に次の入力を受け付けられる状態になることも確認しました。

なお、この時点の確認では開発用モックを使用しています。
表示された文字起こし結果とGemini返答は、実際のSTTやGeminiによる結果ではなく、開発用に用意した固定の内容です。

### チャットUIの表示問題への対応

チャットの最後のカードが画面下部に隠れる問題がありました。

調査の結果、スクロール対象とカードの配置領域の扱いが複雑になっていたため、
`ItemsControl`に任せる構成から、スクロール対象を明示的な縦配置領域にする構成へ変更しました。

- `ScrollViewer`の中にメッセージ用の`StackPanel`を配置
- メッセージカードを明示的に追加
- 最後尾カードの下に終端領域を配置
- 終端領域の大きさは、画面の状態に応じて計算
- 最後のカードが画面下部に隠れないように調整
- 調査用のビルド確認表示やスクロールデバッグ表示は削除

この対応により、最後のカードまで表示できることを確認しました。

### 今日のビルド確認

- Backendビルド成功
- Desktopビルド成功
- AVFoundation用のmacOSネイティブ録音ライブラリ生成成功
- Avaloniaアプリ起動確認
- 実マイク録音から開発用STT、Gemini、TTS、音声再生までの動作確認成功

## 現在の構成

- Backend: ASP.NET Core
- Frontend: Avalonia Desktop
- Database: MySQL on Docker Compose
- 共通契約: `STSApp.Contracts`
- 認証: なし
- 通信方式:
  - REST: 会話作成、履歴取得、音声アップロード、音声取得
  - SignalR: 処理状態通知

## プロジェクト構成

- `STSApp.Backend`
  - ASP.NET Core Backend
  - MySQL、STT、Vector RAG、Gemini、TTS、音声保存、SignalR通知を担当
- `STSApp.Desktop`
  - Avalonia Desktopアプリ
  - チャットUI、VADによる発話・終話検知、録音、Backend通信、SignalR受信、音声再生を担当
- `STSApp.Contracts`
  - DTO、enum、SignalRイベント、Request/Responseを共有
- `docker-compose.yml`
  - MySQL開発環境

## Backendで実装済み

- Controller方式でREST APIを実装
- MySQL接続と初期スキーマ作成
- Docker ComposeによるMySQL起動
- 会話セッション作成
- 会話一覧取得
- 会話ターン取得
- 音声アップロード
- 保存済み音声取得
- DBヘルスチェック
- SignalR Hub
  - `/hubs/conversation`
- Workflow
  - 音声アップロード
  - STT
  - Vector RAG検索
  - Gemini
  - TTS
  - 音声ファイル保存
  - DBイベント保存
  - SignalR通知
- 開発環境限定のRAG資料再取り込みAPI
  - `POST /api/development/rag/reindex`

## DBで実装済み

主なテーブル:

- `conversations`
- `conversation_turns`
- `audio_files`
- `turn_events`
- `knowledge_documents`
- `knowledge_chunks`
- `knowledge_chunk_embeddings`
- `turn_rag_references`

設計方針:

- 会話やターンなど外部に見える可能性があるIDはUUID
- 内部イベントログのIDはBIGINT
- 音声ファイル本体はDBに入れず、ファイルとして保存
- DBには音声ファイルの参照情報を保存
- 入力音声は `storage/audio/input/YYYYMMDD`
- 出力音声は `storage/audio/output/YYYYMMDD`
- VoiceLink資料、チャンク、Embedding、回答時の参照スナップショットを保存

## 外部API連携の実装状況

### 開発用モック

実装済み:

- `ExternalApis:UseDevelopmentMocks` を `true` にすると、STT/TTS/Geminiを固定応答へ切り替えられる
- 開発用STTは固定の文字起こしテキストを返す
- 開発用Geminiは固定の返答テキストを返す
- 開発用TTSは短い無音WAVを返す
- RAGは検索結果の正しさを確認する必要があるため、開発用モックへ切り替えない

用途:

- RAG追加前の録音、音声保存、DB保存、SignalR通知、チャット表示、音声取得・再生の確認に使用していた
- 現在はRAG段階で明確な設定エラーになるため、`UseDevelopmentMocks=true`だけでは会話処理を最後まで完了できない

使い方:

- `STSApp.Backend/appsettings.Development.json` の `ExternalApis` 直下に `"UseDevelopmentMocks": true` を入れる
- 実APIとVector RAGを使う現在の通常確認では `"UseDevelopmentMocks": false` にする

### STT

実装済み:

- `POST <STT_API_BASE_URL>/transcribe`
- `multipart/form-data`
- file field: `file`
- query: `decoding_type`
- Response:
  - `text`
  - `confidence`
  - `duration`

確認状況:

- 実APIへの音声送信と文字起こしを確認済み

### Gemini

実装済み:

- Backend側でAPIキーを管理する前提
- ユーザー発話と直近履歴を渡す
- 返答テキストをDB保存
- SignalRで返答テキスト完了通知

確認状況:

- 実APIによる返答テキスト生成を確認済み
- 実際の値はGit管理対象外のローカル設定で管理

### TTS

実装済み:

- `POST <TTS_API_BASE_URL>/speak`
- JSON request
  - `text`
  - `voicepack`
  - `alpha`
  - `beta`
  - `speed`
- WAV binary response想定
- HTTP 200でも、実データがWAVとして成立するかを保存前に検証
- 返答音声を保存
- `audio_files.kind = output` としてDB保存
- SignalRで `audioId` を通知

確認状況:

- 実APIによるWAV生成と自動再生を確認済み
- 実際の値はGit管理対象外のローカル設定で管理

### Vector RAG

実装済み:

- VoiceLinkの架空資料5件を見出し単位の26チャンクへ分割
- Gemini Embedding APIで768次元のEmbeddingを生成
- MySQLへ資料、チャンク、Embeddingを保存
- ユーザー発話と近い資料を最大3件検索
- 資料が見つかった場合は資料を根拠としてGeminiへ渡す
- 資料が見つからない一般質問は一般知識による回答として区別
- 回答時に使った資料内容を参照スナップショットとして保存

## Avalonia Desktopで実装済み

- チャットUI
- Backend REST接続
  - 会話作成
  - 履歴取得
  - 音声アップロード
  - 音声取得
- SignalR接続
  - `turnStatusChanged`
  - `transcriptionCompleted`
  - `assistantTextCompleted`
  - `speechSynthesisCompleted`
  - `turnFailed`
- VADによる連続音声入力
  - `音声入力開始`を押すとマイク入力の待機を開始
  - WebRTC VADで発話開始を検知
  - 一定時間の無音から終話を検知
  - 1発話をWAVとしてBackendへ自動送信
  - 返答音声再生後、セッションが有効なら次の発話待機へ戻る
- macOS AVFoundationによる実マイク録音
  - 16kHz
  - mono
  - 16bit PCM
  - WAV形式へ変換
- macOS `afplay` によるWAV再生
- Desktop側のBackend URL設定ファイル化
  - `STSApp.Desktop/appsettings.json`
  - `backendBaseUrl`

## 確認済み

- Backendビルド成功
- Desktopビルド成功
- Avaloniaアプリ起動確認
- VADによる発話開始・終話検知を確認
- 実STTによる文字起こしを確認
- Vector RAG検索とGemini返答生成を確認
- 実TTSによるWAV生成と自動再生を確認
- 入力音声と出力音声が日付別フォルダへ保存されることを確認
- DBに以下が保存されることを確認
  - `conversation_turns`
  - `audio_files`
  - `turn_events`
  - RAG資料、Embedding、参照履歴
- SignalR通知の元になるイベントがDBに保存されることを確認
- Backend自動テスト32件成功
- ソリューション全体のビルドが警告0・エラー0で成功

## 現在の注意点

- 実URL、APIキー、DBパスワードはGit管理対象外の `appsettings.Development.json` で管理する
- `UseDevelopmentMocks=true`ではRAGをモック化しないため、現在の会話処理はRAG段階で停止する
- 過去の手動ファイル削除によって、開発DBには参照先ファイルが存在しない古い音声レコードが残っている
- プロセス強制終了まで含めた孤立音声・一時ファイルの自動回収は未実装
- Avaloniaのビルド確認時は、この環境では次の環境変数を付けている
  - `AVALONIA_TELEMETRY_OPTOUT=1`

## 次回やること

現時点で確認・検討する候補:

- 音声アップロードAPIで許可する形式と最大サイズを決める
- Desktopの再生用一時ファイルを、書き込み失敗時にも削除できるようにする
- 過去の検証で参照切れになった開発DBの音声レコードを整理する
- プロセス強制終了時に残る孤立音声・一時ファイルの回収方針を決める

## 2026-07-16 追加メモ

- Desktop側のBackend URLをコード直書きから `STSApp.Desktop/appsettings.json` へ移動
- `DesktopAppSettings` で起動時に設定を読み込む
- 現在の既定値:
  - `backendBaseUrl`: `http://127.0.0.1:5133`

## 2026-07-16 設定ファイル安全化メモ

- `.gitignore` を追加
- `STSApp.Backend/appsettings.Development.json` はGit管理対象外にする方針
- 実URL、Gemini APIキー、voicepack名、ローカルDBパスワードなどは `STSApp.Backend/appsettings.Development.json` にだけ入れる
- 共有・レビュー用には `STSApp.Backend/appsettings.Development.example.json` を使う
- 録音音声は個人情報や会話内容を含む可能性があるため、`STSApp.Backend/storage/audio/` をGit管理対象外にする
