# manga-pipeline

漫画制作の中間定義を管理する、.NET 10 のローカルバックエンドMVPです。定義を版管理し、承認したリビジョンから生成グラフを実行します。

## 現在できること

- TOML定義の検証・保存、リビジョンとストーリーブランチの管理
- 定義間で明示された設定値の食い違いを検出する連続性チェック
- 承認後の漫画向け概念候補グラフを、本編とは分けて保存
- 変更案の提出・承認、変更範囲に応じたモック生成とキャッシュ再利用
- GGUFモデルを別途指定して動かす、LLamaSharpローカル推論ワーカー
- CLIとREST APIによる操作

生成はモックです。LLM推論ワーカーは追加しましたが、作品APIとの対話・変更案作成フロー、実画像生成、音声入力、モバイルアプリ、リモート認証は未実装です。

## はじめる

.NET 10 SDKが必要です。

```sh
dotnet build MangaPipeline.slnx
dotnet run --project src/MangaPipeline.Cli -- validate samples/one-page
dotnet run --project src/MangaPipeline.Cli -- check-continuity samples/one-page
dotnet run --project src/MangaPipeline.Cli -- create "My Manga" path/to/definitions
dotnet test MangaPipeline.slnx
dotnet run --project src/MangaPipeline.Api
```

## 詳細

- [開発・API操作・保存のガイド](./docs/development.md)
- [中間データフローと設計方針](./docs/backend-data-flow.md)
- [LLM対話・プロバイダー切替の設計案](./docs/decisions/0001-stateless-ai-authoring.md)
- [ローカル推論ワーカーの起動と分離](./docs/local-inference-worker.md)
- [公開前必須チェックリスト](./PRE_RELEASE_CHECKLIST.md)

APIはローカル専用です。認証がないため、LANやインターネットへ公開しないでください。
