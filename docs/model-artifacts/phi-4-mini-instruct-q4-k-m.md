# Phi-4-mini-instruct Q4_K_M model artifact

## Pinned artifact

| Field | Value |
|---|---|
| Base model | `microsoft/Phi-4-mini-instruct` |
| GGUF repository | `unsloth/Phi-4-mini-instruct-GGUF` |
| Quantization | `Q4_K_M` |
| Repository revision | `78eb92a46fc37e6b524df991ed9aca9bc6aa7b80` |
| File | `Phi-4-mini-instruct-Q4_K_M.gguf` |
| SHA-256 | `88c00229914083cd112853aab84ed51b87bdf6b9ce42f532d8c85c7c63b1730a` |
| Size | 2,491,874,272 bytes |
| Retrieved | 2026-10-23 |
| License listed by the model repositories | MIT |
| GGUF source | [Pinned file revision](https://huggingface.co/unsloth/Phi-4-mini-instruct-GGUF/resolve/78eb92a46fc37e6b524df991ed9aca9bc6aa7b80/Phi-4-mini-instruct-Q4_K_M.gguf) |
| Base model license | [Microsoft Phi-4-mini-instruct license](https://huggingface.co/microsoft/Phi-4-mini-instruct/raw/main/LICENSE) |

The GGUF is a third-party conversion of Microsoft's base model. The license is a condition on using the model; it does not guarantee copyright ownership or exclusivity for generated text.

## Integrity and security status

The downloaded file's SHA-256 matched the pinned repository metadata. The inference worker verifies this hash before loading the model. Keep the model file outside the repository.

At the pinned revision, Hugging Face metadata reports the Protect AI scan as `safe` while also stating that the file is vulnerable to `PAIT-GGUF-100`; the VirusTotal status is `unscanned`. The meaning of `PAIT-GGUF-100` has not been independently verified. The file has not been loaded or used for inference. Do not execute it until this finding is understood and the artifact is approved for use.

## Alternative GGUF review

Repository and file metadata checked on 2026-10-23:

| Candidate | Revision | Q4_K_M SHA-256 | File-level finding | Decision |
|---|---|---|---|---|
| [bartowski/microsoft_Phi-4-mini-instruct-GGUF](https://huggingface.co/bartowski/microsoft_Phi-4-mini-instruct-GGUF/tree/7ff82c2aaa4dde30121698a973765f39be5288c0) | `7ff82c2aaa4dde30121698a973765f39be5288c0` | `01999f17c39cc3074afae5e9c539bc82d45f2dd7faa3917c66cbef76fce8c0c2` | Protect AI reports `PAIT-GGUF-100`; VirusTotal reports 0/74 detections; JFrog reports safe | Not selected; the same unresolved Protect AI finding remains |
| [lmstudio-community/Phi-4-mini-instruct-GGUF](https://huggingface.co/lmstudio-community/Phi-4-mini-instruct-GGUF/tree/009f2b81869a0afd6b9aae23d29fa7b7ee2dcac9) | `009f2b81869a0afd6b9aae23d29fa7b7ee2dcac9` | `3c4d3cbdf3006d81444f6c7a5a56eb93d8e0f0e2ba5963b8ab62f9fd42604233` | Protect AI reports `PAIT-GGUF-100`; VirusTotal reports 0/74 detections; JFrog reports safe | Not selected; the same unresolved Protect AI finding remains |

Neither candidate is an acceptable replacement while the Protect AI finding is unexplained. The alternative repositories also do not declare a repository-level license tag; the base Microsoft model is MIT-licensed, but that alone does not settle all conversion-artifact provenance questions.

## Chat format

The GGUF metadata declares the Phi-4 chat format: each message is encoded as `<|role|>content<|end|>`, followed by `<|assistant|>` for generation. The worker implements that format and constrains JSON-mode output to JSON syntax; application-level schema and semantic validation remain necessary.
