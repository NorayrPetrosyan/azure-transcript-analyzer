# Codex Instructions

This project was originally generated with Claude Code.

## Project purpose

Azure AI Transcript Analyzer is a local web app for extracting structured PII attributes from call-center transcripts in English and Armenian.

Current stack:
- Frontend: React/Vite/JavaScript
- Backend: Python/FastAPI
- Azure AI Language for PII/entity extraction
- Regex fallback extraction
- Optional Azure OpenAI-related service code may exist in backend services

## Important rules

- Do not rewrite the whole project unless explicitly requested.
- Do not delete the current Python backend unless explicitly requested.
- Before editing, inspect the project and explain the plan.
- Prefer small, reviewable changes.
- Keep the frontend API contract stable unless requirements say otherwise.
- Do not commit secrets, API keys, `.env`, credentials, real transcripts, real PII, `venv`, or `node_modules`.
- Use sanitized sample transcripts only.
- If migrating backend to C#, create a new folder named `backend-dotnet` first.
- Keep the old `backend/` folder until the C# backend is tested.
- After changes, explain what files changed and how to run/test the app.

## Local run commands

Backend:

```bash
cd backend
source venv/bin/activate
pip install -r requirements.txt
uvicorn main:app --reload --port 8000