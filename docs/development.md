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

`check-continuity` は、プロジェクト作成前のTOML定義フォルダーを直接照合します。定義中の同じ事実IDに異なる値があれば、競合した定義と値を表示して終了コード `1` を返します。次のコマンドはサンプルの明示設定を照合します。

```sh
dotnet run --project src/MangaPipeline.Cli -- check-continuity samples/one-page
```

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

## 漫画向け概念候補を保存する

`POST /api/projects/{id}/concept-candidates` は、作者が確認した概念候補を型付きグラフとして保存します。APIは自由入力や会話ログを受け取りません。LLMからの整理案は作者が確認した後にこのAPIへ渡し、候補を保存しても本編の定義やリビジョンは変更されません。

```json
{
  "actor": "author",
  "elements": [
    { "id": "seed", "kind": "core-idea", "label": "記憶を売買する古本屋" },
    { "id": "theme", "kind": "theme", "label": "記憶と自己同一性" },
    { "id": "book", "kind": "motif", "label": "古い本", "description": "売られた記憶の象徴" }
  ],
  "relations": [
    { "fromElementId": "seed", "kind": "expresses", "toElementId": "theme" },
    { "fromElementId": "book", "kind": "appears-as", "toElementId": "seed" }
  ],
  "openQuestions": ["記憶を失った人物は、何を自分らしさの根拠にするのか？"]
}
```

現行の初期語彙は `core-idea`、`theme`、`character`、`setting`、`conflict`、`event`、`scene`、`panel`、`motif` と、`expresses`、`embodies`、`contrasts-with`、`causes`、`occurs-in`、`involves`、`appears-as` です。少なくとも一つの `core-idea` が必要で、関係は存在する要素のみを参照できます。

`GET /api/projects/{id}/concept-candidates` で候補を一覧し、`GET /api/projects/{id}/concept-candidates/{candidateId}` で個別に取得できます。候補は本筋へ自動昇格しません。LLM対話、候補の編集・昇格、生成成果物レビューとの関連付けは未実装です。

## 設定の連続性を確認する

複数の定義にまたがる明示的な設定値は、各ドキュメントの `[content.continuity]` に同じ事実IDで記録できます。たとえば、人物と物語の両方に `"character.mina.name" = "ミナ"` を記録すると、同じ事実IDに異なる値がある場合だけ競合として返します。

```toml
[content.continuity]
"character.mina.name" = "ミナ"
"character.mina.home_town" = "海辺の町"
```

`GET /api/projects/{id}/revisions/{revisionId}/continuity` は指定リビジョンの文書群を横断して照合し、競合した事実IDとそれぞれの値・出典ドキュメントを返します。これは明示的な値の一致・不一致を検出する機能であり、自由文の意味的な矛盾や重複を推測するものではありません。定義やリビジョンは変更しません。

## 保存と生成

- 作業データはOSのユーザー別アプリケーションデータ領域にある `MangaPipeline` に保存します。場所は `MANGA_PIPELINE_DATA` で変更できます。
- 各TOML定義はドキュメントIDと内容のSHA-256ごとに不変保存され、リビジョンはそのハッシュを参照します。
- ジョブキュー、生成物メタデータ、キャッシュ索引にはSQLiteを使います。キャッシュを削除しても、定義や生成済み・採用済み成果物は削除されません。
- 生成バックエンドはモックです。入力と依存が同じノードはキャッシュを再利用し、変更の影響を受ける範囲を再実行します。
- ジョブレポートには入力サイズ、準備・生成・保存時間、キャッシュ利用状況を記録します。

## 制限と安全

実画像生成、AIによる自由文からの変更案作成、音声入力、モバイルクライアント、端末間同期、リモート認証は未実装です。既定では `127.0.0.1` のみで待ち受けます。`0.0.0.0` やLAN向けアドレスへ変更して外部公開しないでください。公開が必要な場合は、先に[公開前必須チェックリスト](../PRE_RELEASE_CHECKLIST.md)を完了してください。
