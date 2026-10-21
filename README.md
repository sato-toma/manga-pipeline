# manga-pipeline

漫画制作の中間定義を管理する、.NET 10 のローカルバックエンドMVPです。定義を版管理し、承認したリビジョンから生成グラフを実行します。

## 現在できること

- TOML定義の検証・保存、リビジョンとストーリーブランチの管理
- 承認後の漫画向け概念候補グラフを、本編とは分けて保存
- 変更案の提出・承認、変更範囲に応じたモック生成とキャッシュ再利用
- CLIとREST APIによる操作

生成はモックです。LLMとの対話・概念案の自動作成、実画像生成、音声入力、モバイルアプリ、リモート認証は未実装です。

## はじめる

.NET 10 SDKが必要です。

```sh
dotnet build MangaPipeline.slnx
dotnet run --project src/MangaPipeline.Cli -- validate samples/one-page
dotnet run --project src/MangaPipeline.Cli -- create "My Manga" path/to/definitions
dotnet test MangaPipeline.slnx
dotnet run --project src/MangaPipeline.Api
```

## 詳細

- [開発・API操作・保存のガイド](./docs/development.md)
- [中間データフローと設計方針](./docs/backend-data-flow.md)
- [公開前必須チェックリスト](./PRE_RELEASE_CHECKLIST.md)

APIはローカル専用です。認証がないため、LANやインターネットへ公開しないでください。
