# ADR 0001: Stateless AI authoring and provider routing

- Status: Proposed
- Date: 2026-10-22
- Scope: AI-assisted authoring conversations, structured definition proposals, and model-provider selection

## Context

The current application validates and stores TOML definitions, revisions, change sets, and approvals. It does not call an LLM. In particular, `RawFeedback` is stored alongside a caller-supplied structured change; the server does not interpret the feedback.

Issue #5 requires an authoring flow that asks about ambiguity, proposes structured changes, and leaves comparison and approval to the author. The application is local-first, and unpublished story material may be confidential. The design must not silently send it to a cloud service or let model output modify canonical definitions.

## Proposed decision

1. Expose authoring through the application's REST API. Keep the API contract, orchestration, validation, and revision handling independent of a model vendor.
2. Support two explicitly selectable provider classes:
   - `local`: a dedicated local inference worker, isolated from the API process. The worker may use a NuGet-distributed inference runtime or a containerized model server, with separately provisioned model weights.
   - `cloud`: a configured cloud provider. Gemini API is the initial candidate because Google publishes an API and structured-output documentation.
3. Default to `local`. Cloud use must be explicitly selected for a request, enabled by server configuration, and marked as public/synthetic input. Free cloud use is not allowed for unpublished or confidential work. Never fall back between providers without an explicit request; a fallback could disclose private input.
4. The API is stateless with respect to conversations. The client resubmits the conversation history and draft context on every turn. The API does not create server-side conversation sessions or persist prompts, responses, or unsubmitted proposals.
5. The server reloads canonical definitions from the specified immutable revision. Client-carried conversation context is untrusted and never replaces canonical revision data.
6. The model returns a typed JSON proposal, not TOML text. The server parses and validates it, returns it for author review, and performs no write. Only an explicit author submission creates a ChangeSet; only the existing approval flow creates a new revision and TOML files.
7. Keep the existing local-only API binding. Do not expose provider credentials or arbitrary provider URLs in request bodies. Do not run model inference in the API process if process-level isolation is a requirement.

This is a design proposal, not a claim that an LLM provider or authoring endpoint has been implemented.

## Provider assessment

Research checked against primary documentation on 2026-10-22. Prices, quotas, model availability, and terms can change; recheck them before enabling a provider. This summary is not legal advice.

NuGet registry lookup on 2026-10-22 found `LLamaSharp` and its CPU/CUDA 12 backends at 0.30.0, `Microsoft.ML.OnnxRuntimeGenAI` at 0.18.0, and `Microsoft.Extensions.AI` at 10.11.0.

| Option | Cost and data handling | Assessment |
|---|---|---|
| LLamaSharp NuGet runtime | LLamaSharp 0.30.0 and CPU/CUDA/Vulkan backend packages are distributed via NuGet. The runtime uses native llama.cpp libraries and loads GGUF model weights from a separate file; weights are not the NuGet package and have their own license. No per-request cloud charge, but local compute and storage are required. | Strong .NET candidate. Put it in a separate restricted worker, not the API process, if isolation is a goal. Confirm the selected model's GGUF availability, Japanese quality, output-format control, native backend compatibility, and license. |
| llama.cpp server in an OCI container | The upstream llama.cpp project provides a local OpenAI-compatible server, GGUF support, quantization, several hardware backends, and CPU/GPU hybrid inference. Package the server binary in an OCI image; provision model weights separately and read-only. | Recommended first isolation prototype for a single local worker. It avoids loading native inference libraries into the API process and provides a simple HTTP boundary. Build/pin the image and restrict the container network, mounts, user, and resources. |
| ONNX Runtime GenAI NuGet runtime | `Microsoft.ML.OnnxRuntimeGenAI` 0.18.0 provides .NET/C# inference for separately supplied ONNX model files. The official `generate()` API documentation currently labels the API preview and subject to change. | Candidate if a suitable ONNX export exists. Preview status and model/export compatibility make it a secondary prototype candidate until validated. |
| Microsoft.Extensions.AI | `Microsoft.Extensions.AI` 10.11.0 provides common .NET abstractions for AI services. | Useful for provider-neutral APIs, but it is not itself a local inference engine or an isolation boundary. |
| Ollama in an OCI container | Ollama publishes a container image and documents CPU and GPU deployment. Its local service exposes an HTTP API; model weights remain separate data. | Convenient for local model management and a useful comparison. The image/container provides the isolation boundary only when configured accordingly; Ollama itself is a runtime, not a VM or container. Do not confuse Ollama Cloud with local inference. |
| vLLM in an OCI container | vLLM publishes an OpenAI-compatible serving image and supports model serving from Hugging Face formats with accelerator-aware container deployment. | Consider for multi-user throughput or models/features better supported by its ecosystem. It is a larger Python/PyTorch serving stack and is not the default for a lightweight local single-worker deployment. Confirm hardware and model support first. |
| Gemini API unpaid quota | Some models have a free tier, but Google's current terms say content submitted to Unpaid Services and generated responses may be used to improve products and machine-learning technologies; human reviewers may process inputs and outputs. Google explicitly says not to submit sensitive, confidential, or personal information to Unpaid Services. | Suitable only for non-confidential experiments or synthetic sample data after explicit user choice. Do not make it the default for unpublished manga. |
| Gemini API with billing enabled | Google's terms say prompts and responses are not used to improve products for Paid Services. They may still be logged for safety/legal purposes for a limited period and may be stored transiently or cached in any country where Google or its agents operate. Charges and quotas apply. | A possible opt-in cloud mode when the author accepts external processing and billing. It is not a free/private alternative. |

NuGet packages distribute managed assemblies and, where applicable, native inference backends; they do not create a virtual machine, container, or OS sandbox. They also do not generally bundle multi-gigabyte model weights. Pin and verify the package versions, native backend, model source, model hash, and model-specific license separately.

If execution must be confined to an environment, use a worker boundary. NuGet is suitable for delivering a .NET worker's inference runtime, but is not itself the isolation mechanism. For an OCI deployment, the runtime can instead be a model server image:

```text
MangaPipeline API
    │ bounded JSON over private IPC or a private container network
    ▼
restricted local inference worker / OCI container
    ├── NuGet inference runtime or containerized model server
    └── read-only, separately provisioned model weights
```

The worker should not mount the project workspace or secrets, should have no outbound network access, and should have explicit memory/CPU limits. Permit only the private IPC or inbound worker endpoint needed by the API. The API must not control a privileged container daemon using caller-provided parameters. A separate process without OS restrictions is not sufficient isolation. Containers use OS-level mechanisms and are not equivalent to VMs; mount, network, privilege, and daemon-access choices determine much of the boundary. GPU access and native backend compatibility must be verified in the target deployment.

For the first local proof of concept, compare `llama.cpp` server in a restricted container with a small LLamaSharp-based worker. Keep the worker protocol model-neutral so Ollama or vLLM can be substituted without changing the authoring API. Do not assume every model distributed by any runtime has the same license or data behavior.

The Gemini API terms also restrict use to professional or business purposes, require API users to be at least 18, and prohibit making an API client available through a service directed to or likely to be accessed by people under 18. If the product's audience or distribution changes, reassess provider eligibility before enabling Gemini.

No Japanese-operated hosted API was selected: the sources reviewed did not establish an option that is simultaneously free, generally available for this use, and appropriate for confidential unpublished work. A Japan-origin local model may be evaluated later using the same provider interface and the model's actual license.

### Primary sources

- [Gemini API pricing](https://ai.google.dev/gemini-api/docs/pricing)
- [Gemini API Additional Terms of Service](https://ai.google.dev/gemini-api/terms)
- [Gemini structured output](https://ai.google.dev/gemini-api/docs/structured-output)
- [Gemini API logs policy](https://ai.google.dev/gemini-api/docs/logs-policy)
- [Ollama chat API](https://docs.ollama.com/api/chat)
- [Ollama structured outputs](https://docs.ollama.com/capabilities/structured-outputs)
- [Ollama Gemma 4 model page](https://ollama.com/library/gemma4)
- [Google Gemma terms](https://ai.google.dev/gemma/terms)
- [LLamaSharp on NuGet](https://www.nuget.org/packages/LLamaSharp)
- [LLamaSharp CPU backend on NuGet](https://www.nuget.org/packages/LLamaSharp.Backend.Cpu)
- [LLamaSharp CUDA 12 backend on NuGet](https://www.nuget.org/packages/LLamaSharp.Backend.Cuda12)
- [Microsoft.ML.OnnxRuntimeGenAI on NuGet](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntimeGenAI)
- [Microsoft.Extensions.AI on NuGet](https://www.nuget.org/packages/Microsoft.Extensions.AI)
- [ONNX Runtime GenAI documentation](https://onnxruntime.ai/docs/genai/)
- [llama.cpp upstream project and local server](https://github.com/ggml-org/llama.cpp)
- [Ollama Docker deployment](https://docs.ollama.com/docker)
- [Ollama OpenAI-compatible API](https://docs.ollama.com/api/openai-compatibility)
- [vLLM OpenAI-compatible deployment in Docker](https://docs.vllm.ai/en/latest/deployment/docker/)
- [Docker Engine security](https://docs.docker.com/engine/security/)
- [Docker resource constraints](https://docs.docker.com/engine/containers/resource_constraints/)

## Conversation and context model

Each turn is an ordinary request/response. The client owns the transcript and resends it on the next request:

1. The author sends the latest input, prior user/assistant turns, the selected provider, and an optional authoring mode.
2. For revision work, the request identifies `projectId`, `branchId`, and `baseRevisionId`. The server loads that exact revision and the relevant documents; it does not trust client-provided replacement definitions.
3. Context construction includes the target documents, their references, and only the relevant generation-graph scope. The request is bounded by configured message, document, and input-size limits.
4. The provider receives the transcript and grounded project context for this one call. No provider-side conversation/thread identifier is required or stored.
5. The response contains either clarification questions or a structured proposal. The client displays it and resends the updated transcript/draft context on the next turn.
6. A lost client transcript means the unsubmitted conversation is lost. This is intentional. Once the author submits a proposal as a ChangeSet, existing durable revision/change-set behavior applies.

For long conversations, the client may carry a compact working summary and unresolved questions. Treat these as untrusted context, cap their size, and ground canonical facts again from the immutable revision every turn. Do not treat model summaries as authoritative facts.

The first implementation supports revision-bound editing only:

- **Revise**: identify a branch and base revision, then propose changes to selected existing definitions. A stale base remains subject to the existing conflict behavior when the author submits the ChangeSet.

Starting a new work from an idea with no canonical revision is a follow-up scope. It must not delay the first usable revision-editing flow.

## Proposed API contract

Add one stateless endpoint:

```http
POST /api/projects/{projectId}/authoring/turns
Content-Type: application/json
```

The request should contain:

- `provider`: `local` or `cloud`; required or explicitly defaulted by a documented server policy.
- `branchId` and `baseRevisionId`: identify the immutable source revision.
- `targetDocumentIds` or `targetScope`: bound the project context to relevant definitions.
- `messages`: bounded ordered turns with only `user` and `assistant` roles.
- `draftContext`: optional client-carried working summary and unresolved questions; never authoritative.
- `cloudDataClassification`: required for cloud turns and initially limited to `public_or_synthetic`.

Return a stable response shape:

```json
{
  "provider": "local",
  "model": "configured-model-id",
  "status": "clarification_required",
  "assistantMessage": "主人公がその町を離れたい理由を、もう少し教えてください。",
  "questions": [
    {
      "id": "q1",
      "question": "主人公が町を離れたい理由は何ですか？",
      "why": "人物の動機と物語の目的がまだ結び付いていません。"
    }
  ],
  "proposal": null,
  "draftContext": {
    "unresolvedQuestionIds": ["q1"]
  }
}
```

When a proposal is ready, `status` is `proposal_ready` and `proposal` contains a summary, assumptions, unresolved questions, and typed `ProposedDocumentChange` values. Each change must identify its target document and provide a complete validated `DocumentDefinition`; it must not contain an arbitrary filesystem path or executable action. A response may include concise evidence references to the author's messages or source document IDs, but must not request or expose hidden chain-of-thought.

The client presents the before/after documents and uncertainty to the author. On explicit submission, it calls the existing ChangeSet API and then the existing approval API. The turn endpoint itself never writes TOML, creates a ChangeSet, advances a branch, or approves anything.

## Provider boundary and configuration

Introduce an internal provider interface that accepts the same bounded message/context/schema request and returns structured JSON plus non-sensitive model metadata. Implement provider-specific HTTP/protocol code behind adapters:

- Local adapter: private IPC or an internal container-network endpoint to a restricted inference worker. The worker hosts the selected NuGet runtime or local model server and exposes only the bounded inference request/response contract.
- Cloud adapter: Gemini API structured output using JSON Schema.

Provider output is untrusted even when the provider claims schema-constrained output. Parse it and validate it locally before returning it.

Configuration is server-owned:

- `MangaPipeline:Authoring:DefaultProvider`
- enabled provider allowlist
- `MangaPipeline:Authoring:LocalWorker:Endpoint` and `ModelId` (private IPC/container network only; never request-controlled)
- `MangaPipeline:Authoring:LocalWorker:ModelPath` (read-only worker-visible path; provisioned separately from NuGet)
- Optional `MangaPipeline:Authoring:Ollama:BaseUrl` and `Model` for an explicitly enabled development adapter
- `MangaPipeline:Authoring:Gemini:Model`
- Gemini API key supplied via environment variable, user secrets, or a platform secret store; never commit it or return it from a settings endpoint.

The client may select only an enabled provider class. It must not supply an arbitrary base URL, model path, API key, or headers. This prevents API callers from turning the service into an SSRF proxy or selecting unapproved billing accounts. Provider/model identity may be returned so the author can see where a turn was processed.

## Proposal validation and safety

- Use a strict response schema with explicit statuses: `conversation`, `clarification_required`, and `proposal_ready`.
- If a missing or conflicting fact could materially change a proposal, ask a focused question rather than silently inventing canon. Provisional assumptions must be labelled.
- Validate document metadata, references, graph integrity, and domain-specific constraints with existing validators. Reject unsupported document operations and malformed output with an explicit error; do not salvage partial JSON silently.
- No model output can write files, execute tools, change provider settings, approve a ChangeSet, or mutate a revision.
- Treat user messages and TOML content as untrusted prompt data. Do not interpret embedded instructions as application policy.
- Do not log message bodies, prompts, model responses, API keys, or full definition content. Log request ID, provider/model ID, durations, token counts if available, and failure category.
- Apply request size and timeout limits. Propagate cancellation. Return explicit provider-unavailable, timeout, quota, invalid-output, and validation errors; never silently switch providers.
- Cloud selection must be an informed user choice. The UI/client should show the selected provider and a concise data-handling notice before sending unpublished content.

## Implementation sequence

1. **Runtime and isolation proof**: compare `llama.cpp` server in an OCI container with a LLamaSharp worker using one approved local model. Evaluate Ollama as a convenience alternative and vLLM only if its serving scale or model support is needed. Verify native backend compatibility, model licensing, Japanese quality, resource use, and worker isolation. Do not add inference packages to the API project before choosing the worker boundary.
2. **Provider settings and boundary**: typed configuration, allowlist, secret loading, provider-neutral request/result types, worker readiness without exposing credentials.
3. **Stateless conversation API**: input bounds, transcript validation, revision-grounded context builder, structured response schema, clarification-only path.
4. **Proposal generation**: typed document proposals, local validation, deterministic before/after preview, no persistence.
5. **Author submission path**: connect accepted proposals to ChangeSet creation and explicit approval; preserve existing conflict and revision behavior.
6. **Provider evaluation**: run the same Japanese scenario set against the selected isolated local runtime and configured Gemini, recording schema validity, clarification quality, proposal correctness, latency, resource use, and cloud cost/terms. Use only public or synthetic material for free cloud requests.
7. **Manual acceptance**: confirm a user can clarify an ambiguous premise, review a valid proposal, reject or edit it, and create a revision only after explicit approval. Confirm interrupted turns are safely repeatable and do not create duplicate ChangeSets.

Do not start chat/voice/mobile UI work until the conversation state transitions, privacy notice, and manual acceptance scenarios are agreed, consistent with Issue #5.

## Acceptance criteria for implementation

- Switching `local`/`cloud` changes only the provider adapter; the same request and response schema is used.
- The first release only proposes changes against an existing revision; it does not claim to bootstrap a new project from an idea.
- The local model runs in the selected restricted worker, not in the API process; it cannot read the workspace or secrets and cannot reach external networks during inference.
- With only the local provider configured, no authoring request makes an outbound model-provider call.
- Cloud turns require an explicit public/synthetic classification; unpublished/private work is rejected for the unpaid cloud provider.
- With only a provider disabled or unavailable, the request returns a clear error and does not fall back.
- A turn asks targeted questions when required information is missing and returns a schema-valid proposal when sufficient information is present.
- The response is not persisted. A follow-up works only from client-resubmitted messages/context and the immutable base revision.
- Invalid model output cannot create or alter project files, revisions, ChangeSets, or branch heads.
- Author edits/rejects are supported by the client workflow; explicit submission persists only a ChangeSet; explicit approval alone advances the branch.
- A base-revision race retains the current conflict behavior and does not lose the proposal.
- Logs and error responses contain no prompts, responses, secrets, or full document bodies.
- Manual Japanese scenarios pass for new-project clarification, existing-character revision, conflicting facts, and stale-revision submission.

## Open decisions before implementation

- Maximum transcript/context size and the client-side history compaction policy.
- Worker isolation mechanism: OCI container versus a separately sandboxed OS process, including the target environment's GPU/native-library support.
- Initial local runtime: `llama.cpp` server container versus LLamaSharp NuGet worker; decide after the isolation and model-format proof of concept.
- Exact model artifact, format, source, hash, license, and provisioning/update process.
- Whether to expose provider choice on every API request or only through a project/client setting; either way, cloud use must be visible and local remains the default.
- Eligibility for a paid cloud tier, if confidential cloud processing is later requested.
- Scope and acceptance criteria for create-from-idea after revision-scoped proposals are usable.
