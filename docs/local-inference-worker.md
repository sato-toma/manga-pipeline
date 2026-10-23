# ローカル推論ワーカー

`MangaPipeline.InferenceWorker` は、作品APIから分離したプロセスでLLamaSharpを使い、GGUFモデルへ会話履歴を渡すローカル推論ワーカーです。会話履歴はリクエスト単位で受け取り、保存しません。現時点では作品APIや変更案作成フローには接続していません。

## 現在の利用可否

固定したGGUFには未解明の `PAIT-GGUF-100` スキャン所見があり、このモデルでの推論はまだ実行していません。所見の確認と、外向き通信を遮断できる実行環境の検証が終わるまでは、ワーカーを起動したり入力を送ったりしないでください。詳細は[モデル記録](./model-artifacts/phi-4-mini-instruct-q4-k-m.md)を参照してください。

## モデル

このワーカーは、[固定したPhi-4-mini-instruct Q4_K_M GGUF](./model-artifacts/phi-4-mini-instruct-q4-k-m.md)だけを受け付けます。起動時にファイル全体のSHA-256を照合し、一致しなければモデルを読み込みません。LLamaSharpのNuGetパッケージにモデル重みは含まれません。GGUFファイルはリポジトリ外に置いてください。

## 起動

.NET 10 SDKを利用できるWSL2環境で、ワーカーを起動するアカウントからモデルファイルの読み取りが可能な状態にします。実行時に必要な設定は環境変数または.NET設定で渡します。

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export MangaPipeline__Inference__ModelPath="$HOME/models/Phi-4-mini-instruct-Q4_K_M.gguf"
dotnet run --project src/MangaPipeline.InferenceWorker
```

SHA-256が一致しない、またはモデルが読み込めない場合、ワーカーは起動に失敗します。モデルパスはリクエストから指定できません。既定では `127.0.0.1:5079` のみで待ち受けます。ポートは `MangaPipeline__Inference__Port` で変更できますが、非ループバックへ公開しないでください。固定モデルに関するセキュリティスキャンの注意事項は、モデル記録を確認してください。

起動後、`GET http://127.0.0.1:5079/health` がモデルIDと `ready` を返します。推論は `POST http://127.0.0.1:5079/v1/chat/completions` へ送ります。

```json
{
  "messages": [
    { "role": "user", "content": "曖昧な点があれば、変更案を作る前に質問してください。" }
  ],
  "maxOutputTokens": 256
}
```

履歴は先頭に任意のsystemメッセージ、その後はuser/assistantの交互の発話で構成し、最後はuser発話にします。最大32メッセージ、合計8,192文字、出力1,024トークンまで受け付けます。ワーカーは同時推論を直列化します。入力、応答、モデルパスはアプリケーションログへ記録しません。

`responseFormat` は省略時 `text`、または `json` を指定できます。Phi-4 mini向けのチャットテンプレート（`<|role|>...<|end|>` とassistant生成開始トークン）を使い、`json` ではJSON文法で生成を制約したうえで、返却前にもJSON構文を検証します。これはJSONとしての構文保証であり、変更案の必須フィールドや意味的な正しさまでは保証しません。作品APIに接続する際は、別途スキーマ検証と承認フローが必要です。

## WSL2上の分離

WSL2はLinux環境を提供しますが、既定でWindowsドライブが `/mnt` 以下にマウントされ、ネットワークも利用できます。WSL2を起動しただけで安全なサンドボックスになったとはみなせません。

機密性のある入力で使う前に、少なくとも次を構成・確認してください。

- 推論専用のWSLディストリビューションと、管理者権限を持たない専用Linuxアカウントを使う。
- ワーカーアカウントには、実行ファイルと読み取り専用モデルだけを見せる。作品データ、ワークスペース、秘密情報を配置・マウントしない。
- 専用ディストリビューションの `/etc/wsl.conf` で不要なWindowsドライブの自動マウントとWindowsプロセス連携を無効化する。設定後はディストリビューションを再起動する。
- ワーカーの待受はループバックだけにし、Windowsホスト以外から到達できないことを確認する。
- WSLの外向き通信は既定で可能です。推論に不要な外向き通信をHyper-V Firewallで拒否し、必要な更新時だけ一時的に許可してください。適用範囲と影響を確認し、既存のWSL利用に影響しないように設定します。
- 設定後に、WindowsドライブやWindowsプロセスへのアクセス、外部ネットワークへの接続が遮断されていることを実際に確認する。未確認なら、非公開の作品データを送らない。

WSLとネットワーク設定の詳細は、[WSLの詳細設定](https://learn.microsoft.com/windows/wsl/wsl-config)、[WSLのネットワーク](https://learn.microsoft.com/windows/wsl/networking)、[Hyper-V Firewall](https://learn.microsoft.com/windows/security/operating-system-security/network-security/windows-firewall/hyper-v-firewall)を参照してください。管理者権限を必要とするファイアウォール設定は、このアプリから自動変更しません。

このワーカーはローカルモデル推論専用です。Ollamaのローカル利用もアカウント登録不要ですが、そのAPIもローカルでは認証なしです。ネットワーク公開を避け、クラウドモデルへ自動切替しないでください。
