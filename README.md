# Azure AI Transcript Analyzer

A local web app for analyzing already-transcribed call-center text in English and Armenian. The app structures conversation turns by speaker role and extracts key PII/case attributes such as name, address, phone number, email, and SSN or national ID.

## Current Stack

- Backend: C# / .NET / ASP.NET Core Web API in `backend-dotnet`
- Frontend: React + Vite in `frontend`
- Azure AI Language: PII/entity extraction when configured
- Azure OpenAI: role detection when configured and dynamic clinic/case attribute extraction
- Regex fallback: local extraction when Azure services are missing or fail

The final backend is `backend-dotnet`. There is no Python/FastAPI backend in the final delivery.

## Secrets And Configuration

Never commit secrets, `.env` files, Azure keys, real transcripts, or real PII.

Configure Azure services with environment variables, .NET user secrets, or another local uncommitted secret store:

```bash
export AZURE_LANGUAGE_ENDPOINT="https://<your-language-resource>.cognitiveservices.azure.com/"
export AZURE_LANGUAGE_KEY="<your-language-key>"
export AZURE_OPENAI_ENDPOINT="https://<your-openai-resource>.openai.azure.com/"
export AZURE_OPENAI_KEY="<your-openai-key>"
export AZURE_OPENAI_DEPLOYMENT="<your-deployment-name>"
export MAX_CONCURRENT_CHUNKS=3
export EXTERNAL_REQUEST_TIMEOUT_SECONDS=60
```

If Azure services are not configured, the backend still runs with regex extraction and fallback speaker labels.

## Run The .NET Backend

From the project root:

```bash
cd backend-dotnet
dotnet restore
dotnet run --no-launch-profile --urls http://localhost:8000
```

Backend endpoints:

- `GET http://localhost:8000/health`
- `POST http://localhost:8000/analyze`
- `GET http://localhost:8000/openapi/v1.json`

Successful `POST /analyze` responses are also saved as human-readable TXT files under `backend-dotnet/local-results/`. This folder is created automatically and ignored by Git.

## Run The Frontend

From a second terminal:

```bash
cd frontend
npm install
VITE_API_BASE_URL=http://localhost:8000 npm run dev
```

Open:

```text
http://localhost:5173
```

`VITE_API_BASE_URL` controls which backend the frontend calls. For local development with `backend-dotnet`, use `http://localhost:8000`.

## API Request Format

The .NET backend accepts `language` and `transcriptText`:

```json
{
  "language": "en",
  "transcriptText": "Agent: Hello, how can I help?\nCaller: My name is John Smith."
}
```

```json
{
  "language": "hy",
  "transcriptText": "Գործակալ: Բարև ձեզ։\nԶանգահարող: Իմ անունը Արա Պետրոսյան է։"
}
```

`transcriptText` may contain real newlines or escaped literal newline text such as `\\n`; the backend normalizes both before analysis.

## API Response Shape

```json
{
  "conversation": [
    { "role": "Agent", "text": "Hello, how can I help?" },
    { "role": "Caller", "text": "My name is John Smith." }
  ],
  "extractedAttributes": {
    "name": "John Smith",
    "address": "",
    "dateOfBirth": "",
    "socialSecurityNumber": "",
    "phoneNumber": "",
    "email": "",
    "doctorName": "",
    "conditions": [],
    "medications": [],
    "other": []
  },
  "dynamicAttributes": [
    {
      "key": "appointment_request",
      "label": "Appointment request",
      "value": "Caller wants to book a follow-up with Dr. Smith",
      "category": "appointment",
      "confidence": 0.82,
      "source": "azure-openai",
      "chunkIndex": 0
    }
  ],
  "rawAzureEntities": [],
  "warning": null,
  "roleMethod": "labels"
}
```

`dynamicAttributes` contains Azure OpenAI-detected clinic/case details that do not fit neatly into the fixed `extractedAttributes` fields, such as symptoms, reason for call, appointment requests, callback requests, medication details, allergies, insurance/OHIP/payment issues, pharmacy details, urgent concerns, and follow-up actions.

## Manual Curl Tests

English:

```bash
curl -X POST http://localhost:8000/analyze \
  -H "Content-Type: application/json" \
  -d '{"language":"en","transcriptText":"Agent: Hello, how can I help you?\nCaller: My name is John Smith. My phone number is 555-867-5309 and my email is john.smith@example.com. I live at 123 Main Street. My SSN is 123-45-6789."}'
```

Armenian:

```bash
curl -X POST http://localhost:8000/analyze \
  -H "Content-Type: application/json" \
  -d '{"language":"hy","transcriptText":"Գործակալ: Բարև ձեզ։\nԶանգահարող: Իմ անունը Արա Պետրոսյան է։ Իմ հեռախոսահամարն է +374 91 123456։ Ես ապրում եմ Երևան, Աբովյան փողոց 15։"}'
```

Empty transcript validation:

```bash
curl -i -X POST http://localhost:8000/analyze \
  -H "Content-Type: application/json" \
  -d '{"language":"en","transcriptText":""}'
```

Escaped newline normalization:

```bash
curl -X POST http://localhost:8000/analyze \
  -H "Content-Type: application/json" \
  -d '{"language":"en","transcriptText":"Agent: Hello.\\nCaller: My name is John Smith."}'
```

Dynamic clinic information:

```bash
curl -X POST http://localhost:8000/analyze \
  -H "Content-Type: application/json" \
  -d '{"language":"en","transcriptText":"Agent: How can I help?\nCaller: I have chest tightness and need a follow-up appointment with Dr. Smith next week. Please call me back after 3 PM. I take metformin and I am allergic to penicillin."}'
```

Long transcript testing:

```bash
TRANSCRIPT_CHUNK_SIZE=300 TRANSCRIPT_CHUNK_OVERLAP=40 MAX_CONCURRENT_CHUNKS=2 \
dotnet run --project backend-dotnet/backend-dotnet.csproj --no-launch-profile --urls http://localhost:8000
```

Then send a longer transcript with repeated details across chunks and confirm duplicate `dynamicAttributes` are merged.
