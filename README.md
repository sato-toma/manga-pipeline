# manga-pipeline backend MVP

.NET 10 のローカルバックエンドです。中間定義をTOMLファイルとして版管理し、承認後の特定リビジョンからモック生成グラフを実行します。

## 実行

```sh
dotnet build MangaPipeline.slnx
dotnet run --project src/MangaPipeline.Cli -- validate samples/one-page
dotnet test MangaPipeline.slnx
dotnet run --project src/MangaPipeline.Api
```

APIは `src/MangaPipeline.Api/appsettings.json` の `MangaPipeline:Urls` を既定値として使い、`http://127.0.0.1:5078` のみで待ち受けます。起動時は `ASPNETCORE_URLS` で上書きできます。HTTPSの開発用プロファイルは別途 `https://localhost:7105` を使います。

ポート番号自体はセキュリティ機能ではありません。既定の `127.0.0.1` は同じPCからの接続に限定します。`0.0.0.0` やLAN向けアドレスに変更すると他の端末から到達可能になるため、認証のない現在のAPIを外部公開しないでください。

`POST /api/projects` で1ページのサンプル作品を作成し、返された `id` と `branchHeads.main` を使って以下の流れを試せます。

1. `POST /api/projects/{id}/generation-jobs` で生成ジョブを登録
2. `GET /api/generation-jobs/{jobId}` で完了・プレビュー情報を確認
3. `POST /api/projects/{id}/work-units` でコマ単位の作業を作成
4. `PUT /api/projects/{id}/work-units/{workUnitId}/proposal` で自由文と対象ファイルの変更案を保存
5. `POST /api/projects/{id}/change-sets/{changeSetId}/approve` で承認し、新リビジョンを作成
6. 新リビジョンから再生成し、`GET /api/projects/{id}/revisions/{revisionId}/provenance` で検証可能なマニフェストを取得

競合した変更案は破棄せず `409 Conflict` として保存し、基準リビジョン・変更内容とともに返します。復元は旧リビジョンへ巻き戻すのではなく、新しいリビジョンを作ります。

## 保存と部分実行

- 初期定義: [`samples/one-page/`](./samples/one-page) の1ファイル1ドキュメントTOML。
- 作業データ: OSのユーザー別アプリケーションデータ領域内の `MangaPipeline`。`MANGA_PIPELINE_DATA` で変更できます。
- 定義ファイル: TOMLをドキュメントIDと内容のSHA-256ごとに不変保存し、リビジョンは各ドキュメントのハッシュを参照します。REST APIの入出力と作業メタデータはJSONです。
- 実行索引・ジョブキュー: SQLite。キャッシュの削除は定義や生成済み・採用済み成果物を削除しません。
- 生成: モックバックエンド。対象ノードに必要な定義ファイルだけを読み、同一入力はキャッシュし、変更したノードの下流だけを再実行します。
- 実行計測: 定義読込・依存解決等の準備時間、モック生成時間、成果物保存時間、入力バイト数、キャッシュ利用をジョブレポートに記録します。

実際の画像生成モデル、AIによる自由文の変更案作成、モバイルクライアント側のオフライン編集・同期、リモート認証は未実装です。APIをLANやインターネットへ公開しないでください。
