# 開発・操作ガイド

## 必要な環境

- .NET 10 SDK

## ビルド・検証・起動

リポジトリのルートで実行します。

```sh
dotnet build MangaPipeline.slnx
dotnet run --project src/MangaPipeline.Cli -- validate samples/one-page
dotnet run --project src/MangaPipeline.Cli -- create "My Manga" path/to/definitions
dotnet test MangaPipeline.slnx
dotnet run --project src/MangaPipeline.Api
```

APIを起動すると、開発設定ではブラウザーが `/health` を開きます。既定の待受先は `http://127.0.0.1:5078` です。起動URLは `src/MangaPipeline.Api/appsettings.json` の `MangaPipeline:Urls` で設定し、`ASPNETCORE_URLS` で上書きできます。HTTPSの開発プロファイルは `https://localhost:7105` です。

`validate` は指定フォルダー内のTOML定義を検証します。`create` は同じ検証を行った後、それらの定義からローカルプロジェクトと初期リビジョンを作成します。APIと同じデータ領域を使用するには、APIとCLIの両方で同じ `MANGA_PIPELINE_DATA` を設定してください。未設定時はOSのユーザー別アプリケーションデータ内の `MangaPipeline` を使います。

## 作品を作成する

`POST /api/projects` に作品名を送ります。`documents` を省略すれば同梱の1ページサンプルを使い、指定すれば独自の定義から作成します。定義群は検証後に初期リビジョンへ保存されます。

```json
{
  "name": "My Manga",
  "documents": [
    {
      "id": "world",
      "kind": "world",
      "scope": "project",
      "schemaVersion": 1,
      "content": { "title": "My Manga" },
      "references": []
    },
    {
      "id": "graph",
      "kind": "generation-graph",
      "scope": "project",
      "schemaVersion": 1,
      "content": {
        "nodes": [
          {
            "id": "world",
            "kind": "world",
            "version": 1,
            "inputDocumentIds": ["world"],
            "dependsOnNodeIds": []
          }
        ]
      },
      "references": ["world"]
    }
  ]
}
```

REST APIの入出力はJSONですが、中間定義はTOMLとして保存します。ドキュメントID、参照、`graph` 定義、ノード依存関係は検証され、不正な定義は受け付けません。

## 基本的な制作フロー

作成レスポンスの `id` と `branchHeads.main` を使います。

1. `POST /api/projects/{id}/generation-jobs` で生成ジョブを登録する。
2. `GET /api/generation-jobs/{jobId}` で状態と結果を確認する。
3. `POST /api/projects/{id}/work-units` で対象範囲と完了条件を持つ作業を作る。
4. `PUT /api/projects/{id}/work-units/{workUnitId}/proposal` で自由文と定義変更案を提出する。
5. `POST /api/projects/{id}/change-sets/{changeSetId}/approve` で承認し、新しいリビジョンを作る。
6. 必要に応じて新リビジョンから再生成し、`GET /api/projects/{id}/revisions/{revisionId}/provenance` で来歴を確認する。

基準リビジョンとブランチ先頭が異なる提案は `409 Conflict` として保持されます。過去版の復元も履歴を消さず、新しいリビジョンとして記録されます。

## 保存と生成

- 作業データはOSのユーザー別アプリケーションデータ領域にある `MangaPipeline` に保存します。場所は `MANGA_PIPELINE_DATA` で変更できます。
- 各TOML定義はドキュメントIDと内容のSHA-256ごとに不変保存され、リビジョンはそのハッシュを参照します。
- ジョブキュー、生成物メタデータ、キャッシュ索引にはSQLiteを使います。キャッシュを削除しても、定義や生成済み・採用済み成果物は削除されません。
- 生成バックエンドはモックです。入力と依存が同じノードはキャッシュを再利用し、変更の影響を受ける範囲を再実行します。
- ジョブレポートには入力サイズ、準備・生成・保存時間、キャッシュ利用状況を記録します。

## 制限と安全

実画像生成、AIによる自由文からの変更案作成、音声入力、モバイルクライアント、端末間同期、リモート認証は未実装です。既定では `127.0.0.1` のみで待ち受けます。`0.0.0.0` やLAN向けアドレスへ変更して外部公開しないでください。公開が必要な場合は、先に[公開前必須チェックリスト](../PRE_RELEASE_CHECKLIST.md)を完了してください。
