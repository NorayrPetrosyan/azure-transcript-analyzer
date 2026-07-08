# Azure AI Transcript Analyzer .NET Backend

ASP.NET Core Web API backend for transcript role detection and PII/entity extraction.

## Run

```bash
cd backend-dotnet
dotnet restore
dotnet run --no-launch-profile --urls http://localhost:8000
```

OpenAPI is available in development at:

```text
http://localhost:8000/openapi/v1.json
```

Interactive Swagger UI is available at:

```text
http://localhost:8000/swagger
```

## Configuration

Use environment variables, .NET user secrets, or local appsettings overrides. Do not commit real keys.

```bash
export AZURE_LANGUAGE_ENDPOINT="https://<your-language-resource>.cognitiveservices.azure.com/"
export AZURE_LANGUAGE_KEY="<your-language-key>"
export AZURE_OPENAI_ENDPOINT="https://<your-openai-resource>.openai.azure.com/"
export AZURE_OPENAI_KEY="<your-openai-key>"
export AZURE_OPENAI_DEPLOYMENT="<your-deployment-name>"
export ENABLE_DYNAMIC_AI_EXTRACTION=true
export ENABLE_DYNAMIC_AI_EXTRACTION_IN_LARGE_TRANSCRIPT_SAFE_MODE=false
export ENABLE_LARGE_TRANSCRIPT_SAFE_MODE=true
export LARGE_TRANSCRIPT_THRESHOLD_CHARACTERS=20000
export MAX_CONCURRENT_CHUNKS=3
export AZURE_OPENAI_TIMEOUT_SECONDS=60
export AZURE_LANGUAGE_TIMEOUT_SECONDS=60
```

Azure Language is used for PII/entity extraction. Azure OpenAI is used for role detection only when no explicit Agent/Caller labels exist and for dynamic clinic/case attribute extraction. Regex and Speaker 1/Speaker 2 fallbacks keep the API usable without Azure services.

## Analyze Request

```bash
curl -X POST http://localhost:8000/analyze \
  -H "Content-Type: application/json" \
  -d '{"language":"en","transcriptText":"Agent: Hello.\nCaller: My name is John Smith."}'
```

`transcriptText` may contain real newlines or escaped literal newline text such as `\\n`; the backend normalizes both before analysis.

The response keeps the fixed `extractedAttributes` object and also includes `dynamicAttributes` for AI-detected case information such as symptoms, reason for call, appointment requests, callback requests, medication details, allergies, insurance/OHIP/payment issues, pharmacy details, urgent concerns, and follow-up actions.

## Long Transcripts

Long transcripts are split into configurable ordered chunks before Azure AI Language or Azure OpenAI calls. Chunking prefers conversation line boundaries and includes a small line overlap to avoid losing context at boundaries.

```bash
export CHUNK_MAX_CHARACTERS=4000
export CHUNK_OVERLAP_LINES=6
export MAX_CONCURRENT_CHUNKS=3
```

When `ENABLE_LARGE_TRANSCRIPT_SAFE_MODE=true` and transcript length is at or above `LARGE_TRANSCRIPT_THRESHOLD_CHARACTERS`, the backend skips Azure OpenAI role detection and uses explicit labels or fallback speaker labels instead. Local/context extraction runs first, Azure Language still runs on chunks, and dynamic OpenAI extraction is skipped by default so stable fixed attributes are prioritized. Set `ENABLE_DYNAMIC_AI_EXTRACTION_IN_LARGE_TRANSCRIPT_SAFE_MODE=true` only when you want long transcripts to call OpenAI for dynamic case-related attributes.

Chunk-level extraction results are merged into one final `extractedAttributes` object plus ordered, deduplicated `dynamicAttributes`. Azure AI Language, role detection for shorter unlabeled transcripts, and dynamic attribute chunks use bounded concurrency controlled by `MAX_CONCURRENT_CHUNKS`. If one chunk fails, the API returns partial results with a warning instead of failing the whole request. The response includes `processingInfo` with chunk count, safe-mode status, retries, fallback chunks, and duration timings.

## Local TXT Outputs

Each successful `POST /analyze` response is saved as a human-readable TXT file under `backend-dotnet/local-results/`. The folder is created automatically and is ignored by Git. These files are for local development/testing only; do not use real transcripts or real PII in committed files.

TXT outputs include fixed extracted attributes, dynamic attributes, raw Azure entities, conversation turns, and warnings.

## Manual Test Scenarios

1. Short English transcript with explicit `Agent:` / `Caller:` labels.
2. Short Armenian transcript with `Գործակալ:` / `Զանգահարող:` labels.
3. Long English transcript with `TRANSCRIPT_CHUNK_SIZE=300` to force repeated details across chunks.
4. Transcript mentioning symptoms, medication, appointment request, and callback request.
5. Azure OpenAI unavailable: leave OpenAI settings blank/placeholders and confirm fixed Azure/regex extraction still returns with a warning.
6. Empty transcript validation: send `{"language":"en","transcriptText":""}` and expect `400`.
