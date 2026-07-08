namespace TranscriptAnalyzer.Services;

public static class OpenApiDocumentFactory
{
    public static object Create() => new
    {
        openapi = "3.0.3",
        info = new
        {
            title = "Azure AI Transcript Analyzer .NET API",
            version = "1.0.0"
        },
        paths = new Dictionary<string, object>
        {
            ["/health"] = new
            {
                get = new
                {
                    summary = "Backend health and Azure configuration status",
                    responses = new Dictionary<string, object>
                    {
                        ["200"] = new
                        {
                            description = "Health response"
                        }
                    }
                }
            },
            ["/analyze"] = new
            {
                post = new
                {
                    summary = "Analyze a call-center transcript",
                    requestBody = new
                    {
                        required = true,
                        content = new Dictionary<string, object>
                        {
                            ["application/json"] = new
                            {
                                schema = new
                                {
                                    type = "object",
                                    required = new[] { "transcriptText" },
                                    properties = new Dictionary<string, object>
                                    {
                                        ["language"] = new { type = "string", example = "en" },
                                        ["transcriptText"] = new { type = "string", example = "Agent: Hello.\\nCaller: My name is John Smith." }
                                    }
                                }
                            }
                        }
                    },
                    responses = new Dictionary<string, object>
                    {
                        ["200"] = new
                        {
                            description = "Transcript analysis response",
                            content = new Dictionary<string, object>
                            {
                                ["application/json"] = new
                                {
                                    schema = AnalyzeResponseSchema()
                                }
                            }
                        },
                        ["400"] = new
                        {
                            description = "Invalid request"
                        }
                    }
                }
            }
        }
    };

    private static object AnalyzeResponseSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["conversation"] = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["role"] = new { type = "string" },
                        ["text"] = new { type = "string" }
                    }
                }
            },
            ["extractedAttributes"] = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["name"] = new { type = "string" },
                    ["address"] = new { type = "string" },
                    ["dateOfBirth"] = new { type = "string" },
                    ["socialSecurityNumber"] = new { type = "string" },
                    ["phoneNumber"] = new { type = "string" },
                    ["email"] = new { type = "string" },
                    ["doctorName"] = new { type = "string" },
                    ["conditions"] = new { type = "array", items = new { type = "string" } },
                    ["medications"] = new { type = "array", items = new { type = "string" } },
                    ["other"] = new { type = "array", items = new { type = "string" } }
                }
            },
            ["dynamicAttributes"] = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["key"] = new { type = "string", example = "appointment_request" },
                        ["label"] = new { type = "string", example = "Appointment request" },
                        ["value"] = new { type = "string", example = "Caller wants to book a follow-up with Dr. Smith" },
                        ["category"] = new { type = "string", example = "appointment" },
                        ["confidence"] = new { type = "number", format = "double", example = 0.82 },
                        ["source"] = new { type = "string", example = "azure-openai" },
                        ["chunkIndex"] = new { type = "integer", example = 2 }
                    }
                }
            },
            ["rawAzureEntities"] = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["text"] = new { type = "string" },
                        ["category"] = new { type = "string" },
                        ["subcategory"] = new { type = "string" },
                        ["confidence"] = new { type = "number", format = "double" }
                    }
                }
            },
            ["warning"] = new { type = "string", nullable = true },
            ["roleMethod"] = new { type = "string" },
            ["processingInfo"] = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["usedChunking"] = new { type = "boolean", example = true },
                    ["chunkCount"] = new { type = "integer", example = 8 },
                    ["language"] = new { type = "string", example = "hy" },
                    ["largeTranscriptSafeMode"] = new { type = "boolean", example = true },
                    ["largeTranscriptThresholdCharacters"] = new { type = "integer", example = 20000 },
                    ["failedOpenAiChunks"] = new { type = "integer", example = 1 },
                    ["openAiFailedChunks"] = new { type = "array", items = new { type = "integer" }, example = new[] { 1, 2, 3 } },
                    ["dynamicAiSkippedChunks"] = new { type = "array", items = new { type = "integer" }, example = new[] { 1, 2, 3 } },
                    ["fallbackUsed"] = new { type = "boolean", example = true },
                    ["retriedOpenAiChunks"] = new { type = "integer", example = 1 },
                    ["usedFallbackForChunks"] = new { type = "array", items = new { type = "integer" }, example = new[] { 2 } },
                    ["totalDurationMs"] = new { type = "integer", format = "int64", example = 15320 },
                    ["azureOpenAiDurationMs"] = new { type = "integer", format = "int64", example = 9000 },
                    ["azureLanguageDurationMs"] = new { type = "integer", format = "int64", example = 3000 },
                    ["localExtractionDurationMs"] = new { type = "integer", format = "int64", example = 200 }
                }
            }
        }
    };
}
