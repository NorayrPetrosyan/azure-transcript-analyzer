using System.Text.RegularExpressions;
using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed partial class RegexExtractionService
{
    public ExtractedAttributes Extract(string text, IReadOnlyList<ConversationTurn>? conversationTurns = null)
    {
        var attrs = new ExtractedAttributes();

        attrs.Email = FirstValue(EmailRegex().Matches(text));
        attrs.PhoneNumber = CleanPhone(FirstValue(PhoneRegex().Matches(text)));
        attrs.SocialSecurityNumber = CleanSsn(FirstValue(SsnRegex().Matches(text)));
        if (string.IsNullOrWhiteSpace(attrs.SocialSecurityNumber))
        {
            attrs.SocialSecurityNumber = FirstValue(ArmenianIdRegex().Matches(text));
        }
        if (string.IsNullOrWhiteSpace(attrs.SocialSecurityNumber))
        {
            attrs.SocialSecurityNumber = FirstCapturedValue(GenericIdRegex().Matches(text));
        }
        if (string.IsNullOrWhiteSpace(attrs.SocialSecurityNumber))
        {
            attrs.SocialSecurityNumber = FirstCapturedValue(ArmenianGenericIdRegex().Matches(text));
        }

        var nameMatch = NameRegex().Match(text);
        if (nameMatch.Success)
        {
            attrs.Name = TrimArmenianVerb(nameMatch.Groups[1].Value.Trim());
        }

        attrs.Name = PreferBetterName(attrs.Name, ExtractNameFromContext(text, conversationTurns));

        attrs.Address = ExtractAddressFromContext(text, conversationTurns);

        var dobMatch = DateOfBirthRegex().Match(text);
        if (dobMatch.Success)
        {
            attrs.DateOfBirth = dobMatch.Groups["date"].Value.Trim().TrimEnd('.', ',', ';');
        }

        var contextualDob = ExtractDateOfBirthFromContext(text, conversationTurns);
        if (!string.IsNullOrWhiteSpace(contextualDob))
        {
            attrs.DateOfBirth = contextualDob;
        }

        var doctorMatch = DoctorRegex().Match(text);
        if (doctorMatch.Success)
        {
            attrs.DoctorName = NormalizeDoctorName(doctorMatch.Value);
        }
        attrs.DoctorName = PreferLongerValue(attrs.DoctorName, ExtractDoctorFromContext(text, conversationTurns));

        attrs.Medications.AddRange(ExtractMedications(text));
        attrs.Conditions.AddRange(ExtractConditions(text));

        return attrs;
    }

    public ExtractedAttributes Merge(params ExtractedAttributes?[] sources)
    {
        var merged = new ExtractedAttributes();
        var validSources = sources.Where(source => source is not null).Cast<ExtractedAttributes>();

        foreach (var source in validSources)
        {
            merged.Name = PreferBetterName(merged.Name, source.Name);
            merged.Address = PreferBetterAddress(merged.Address, source.Address);
            merged.SocialSecurityNumber = PreferBetterIdentifier(merged.SocialSecurityNumber, source.SocialSecurityNumber);
            merged.PhoneNumber = FirstNonEmpty(merged.PhoneNumber, source.PhoneNumber);
            merged.Email = FirstNonEmpty(merged.Email, source.Email);
            merged.DateOfBirth = PreferBetterDateOfBirth(merged.DateOfBirth, source.DateOfBirth);
            merged.DoctorName = PreferLongerValue(merged.DoctorName, source.DoctorName);

            AddDistinct(merged.Conditions, source.Conditions.Where(IsCleanCondition));
            AddDistinct(merged.Medications, source.Medications.Where(IsCleanMedication));

            foreach (var item in source.Other)
            {
                if (!merged.Other.Contains(item, StringComparer.OrdinalIgnoreCase))
                {
                    merged.Other.Add(item);
                }
            }
        }

        merged.SocialSecurityNumber = CleanSsn(merged.SocialSecurityNumber);
        merged.PhoneNumber = CleanPhone(merged.PhoneNumber);
        merged.Email = CleanEmail(merged.Email);
        return merged;
    }

    private static string FirstValue(MatchCollection matches) =>
        matches.Count > 0 ? matches[0].Value.Trim() : string.Empty;

    private static string FirstCapturedValue(MatchCollection matches)
    {
        if (matches.Count == 0)
        {
            return string.Empty;
        }

        var match = matches[0];
        return match.Groups["value"].Success
            ? match.Groups["value"].Value.Trim().TrimEnd('.', ',', ';')
            : match.Value.Trim();
    }

    private static string FirstNonEmpty(string current, string candidate) =>
        string.IsNullOrWhiteSpace(current) ? candidate.Trim() : current;

    private static string TrimArmenianVerb(string value) =>
        value.EndsWith(" է", StringComparison.Ordinal) ? value[..^2].Trim() : value;

    private static string PreferBetterName(string current, string candidate)
    {
        current = current.Trim();
        candidate = candidate.Trim();
        if (string.IsNullOrWhiteSpace(current))
        {
            return candidate;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return current;
        }

        var currentScore = NameScore(current);
        var candidateScore = NameScore(candidate);
        return candidateScore > currentScore ? candidate : current;
    }

    private static int NameScore(string value)
    {
        var words = Regex.Matches(value, @"\b[A-ZԱ-Ֆ][A-Za-zԱ-Ֆա-ֆ']+\.?\b")
            .Select(match => match.Value)
            .ToList();

        var score = words.Count * 10 + value.Length;
        if (words.Count >= 2)
        {
            score += 30;
        }

        if (Regex.IsMatch(value, @"\b[A-Z]\.\b"))
        {
            score += 10;
        }

        return score;
    }

    private static string PreferLongerValue(string current, string candidate)
    {
        current = current.Trim();
        candidate = candidate.Trim();
        if (string.IsNullOrWhiteSpace(current))
        {
            return candidate;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return current;
        }

        return candidate.Length > current.Length ? candidate : current;
    }

    private static string PreferBetterAddress(string current, string candidate)
    {
        current = current.Trim();
        candidate = candidate.Trim();
        if (string.IsNullOrWhiteSpace(current))
        {
            return candidate;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return current;
        }

        return AddressScore(candidate) > AddressScore(current) ? candidate : current;
    }

    private static int AddressScore(string value)
    {
        var score = value.Length;
        if (PostalCodeRegex().IsMatch(value))
        {
            score += 60;
        }

        if (Regex.IsMatch(value, @"\b(?:apartment|apt|unit|suite|բնակարան)\b", RegexOptions.IgnoreCase))
        {
            score += 30;
        }

        if (Regex.IsMatch(value, @"\b(?:toronto|ontario|street|st\.?|avenue|ave\.?|road|rd\.?|west|east|north|south|Տորոնտո|Օնտարիո)\b", RegexOptions.IgnoreCase))
        {
            score += 20;
        }

        return score;
    }

    private static string PreferBetterIdentifier(string current, string candidate)
    {
        current = current.Trim();
        candidate = candidate.Trim();
        if (string.IsNullOrWhiteSpace(current))
        {
            return candidate;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return current;
        }

        var currentScore = IdentifierScore(current);
        var candidateScore = IdentifierScore(candidate);
        return candidateScore > currentScore ? candidate : current;
    }

    private static int IdentifierScore(string value)
    {
        var digits = NonDigitRegex().Replace(value, "");
        var score = value.Length;
        if (Regex.IsMatch(value, @"^\d{3}-\d{2}-\d{4}$"))
        {
            score += 100;
        }

        if (digits.Length >= 8)
        {
            score += 40;
        }

        if (Regex.IsMatch(value, @"[A-Za-z]"))
        {
            score += 20;
        }

        return score;
    }

    private static string PreferBetterDateOfBirth(string current, string candidate)
    {
        current = current.Trim();
        candidate = candidate.Trim();
        if (string.IsNullOrWhiteSpace(current))
        {
            return candidate;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return current;
        }

        return DateScore(candidate) > DateScore(current) ? candidate : current;
    }

    private static int DateScore(string value)
    {
        var score = value.Length;
        if (YearRegex().IsMatch(value))
        {
            score += 30;
        }

        if (MonthNameRegex().IsMatch(value) || NumericDateRegex().IsMatch(value) || IsoDateRegex().IsMatch(value))
        {
            score += 30;
        }

        return score;
    }

    private static string ExtractNameFromContext(
        string text,
        IReadOnlyList<ConversationTurn>? conversationTurns)
    {
        var best = string.Empty;
        foreach (Match match in NameContextRegex().Matches(text))
        {
            var candidates = CandidateNameRegex().Matches(match.Groups["value"].Value)
                .Select(candidate => candidate.Value.Trim().TrimEnd('.', ',', ';'))
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate));

            foreach (var candidate in candidates)
            {
                best = PreferBetterName(best, candidate);
            }
        }

        var lines = BuildContextLines(text, conversationTurns);
        const int lookahead = 3;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = RemoveSpeakerLabel(lines[i]);
            if (!NamePromptRegex().IsMatch(line))
            {
                continue;
            }

            foreach (var answer in lines.Skip(i + 1).Take(lookahead).Select(RemoveSpeakerLabel))
            {
                foreach (Match candidate in CandidateNameRegex().Matches(answer))
                {
                    best = PreferBetterName(best, candidate.Value.Trim().TrimEnd('.', ',', ';'));
                }
            }
        }

        return best;
    }

    private static void AddDistinct(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values.Select(v => v.Trim()).Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (!target.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(value);
            }
        }
    }

    private static string NormalizeDoctorName(string value)
    {
        var cleaned = value.Trim().TrimEnd('.', ',', ';');
        return cleaned.StartsWith("Dr.", StringComparison.OrdinalIgnoreCase)
            ? $"Dr. {cleaned[3..].Trim()}"
            : cleaned;
    }

    private static string ExtractAddressFromContext(
        string transcript,
        IReadOnlyList<ConversationTurn>? conversationTurns)
    {
        var lines = BuildContextLines(transcript, conversationTurns);
        var best = string.Empty;
        const int lookahead = 4;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = RemoveSpeakerLabel(lines[i]);
            if (!AddressPromptRegex().IsMatch(line) || PharmacyContextRegex().IsMatch(line))
            {
                continue;
            }

            foreach (var candidateLine in lines
                .Skip(i + 1)
                .Take(lookahead)
                .Select(RemoveSpeakerLabel)
                .Where(candidate => !PharmacyContextRegex().IsMatch(candidate)))
            {
                var cleanedLine = CleanupAddress(candidateLine);
                if (IsLikelyPatientAddress(cleanedLine))
                {
                    best = PreferBetterAddress(best, cleanedLine);
                }
            }

            var nearby = string.Join(" ", lines
                .Skip(i)
                .Take(lookahead + 1)
                .Select(RemoveSpeakerLabel)
                .Where(candidate => !PharmacyContextRegex().IsMatch(candidate)));

            foreach (Match match in AddressCandidateRegex().Matches(nearby))
            {
                var candidate = CleanupAddress(match.Value);
                if (IsLikelyPatientAddress(candidate))
                {
                    best = PreferBetterAddress(best, candidate);
                }
            }
        }

        return best;
    }

    private static string CleanupAddress(string value)
    {
        var cleaned = Regex.Replace(value.Trim().TrimEnd('.', ';'), @"\s+", " ");
        cleaned = Regex.Replace(cleaned, @"^(?:my address is|it is|it's|address is|current address is|home address is|հասցեն է|իմ հասցեն է|ներկայիս հասցեն է)\s+", "", RegexOptions.IgnoreCase);
        return cleaned.Trim(' ', ',', ':');
    }

    private static bool IsLikelyPatientAddress(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !PharmacyContextRegex().IsMatch(value)
            && Regex.IsMatch(value, @"\d")
            && Regex.IsMatch(value, @"\b(?:street|st\.?|avenue|ave\.?|road|rd\.?|drive|dr\.?|boulevard|blvd\.?|lane|ln\.?|court|ct\.?|west|east|north|south)\b", RegexOptions.IgnoreCase);
    }

    private static string ExtractDoctorFromContext(
        string transcript,
        IReadOnlyList<ConversationTurn>? conversationTurns)
    {
        var best = string.Empty;
        foreach (Match match in DoctorRegex().Matches(transcript))
        {
            best = PreferLongerValue(best, NormalizeDoctorName(match.Value));
        }

        foreach (Match match in ArmenianDoctorRegex().Matches(transcript))
        {
            best = PreferLongerValue(best, match.Value.Trim().TrimEnd('.', ',', ';', '։'));
        }

        var lines = BuildContextLines(transcript, conversationTurns);
        const int lookahead = 3;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!DoctorPromptRegex().IsMatch(RemoveSpeakerLabel(lines[i])))
            {
                continue;
            }

            foreach (var answer in lines.Skip(i + 1).Take(lookahead).Select(RemoveSpeakerLabel))
            {
                var english = DoctorRegex().Match(answer);
                if (english.Success)
                {
                    best = PreferLongerValue(best, NormalizeDoctorName(english.Value));
                }

                var armenian = ArmenianDoctorRegex().Match(answer);
                if (armenian.Success)
                {
                    best = PreferLongerValue(best, armenian.Value.Trim().TrimEnd('.', ',', ';', '։'));
                }
            }
        }

        return best;
    }

    private static IReadOnlyList<string> ExtractMedications(string text)
    {
        var terms = new List<string>();

        foreach (Match match in MedicationMentionRegex().Matches(text))
        {
            AddCleanMedication(terms, match.Value);
        }

        foreach (Match match in MedicationContextLineRegex().Matches(text))
        {
            foreach (Match medication in MedicationMentionRegex().Matches(match.Groups["items"].Value))
            {
                AddCleanMedication(terms, medication.Value);
            }
        }

        return terms;
    }

    private static void AddCleanMedication(List<string> terms, string value)
    {
        var cleaned = CleanupClinicalTerm(value);
        if (IsCleanMedication(cleaned) && !terms.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
        {
            terms.Add(cleaned);
        }
    }

    private static bool IsCleanMedication(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || BrokenFragmentRegex().IsMatch(value))
        {
            return false;
        }

        return MedicationMentionRegex().IsMatch(value);
    }

    private static IReadOnlyList<string> ExtractConditions(string text)
    {
        var terms = new List<string>();
        foreach (Match match in KnownConditionRegex().Matches(text))
        {
            AddCleanCondition(terms, match.Value);
        }

        foreach (Match match in FamilyHistoryConditionRegex().Matches(text))
        {
            AddCleanCondition(terms, match.Value);
        }

        return terms;
    }

    private static void AddCleanCondition(List<string> terms, string value)
    {
        var cleaned = CleanupClinicalTerm(value);
        if (IsCleanCondition(cleaned) && !terms.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
        {
            terms.Add(cleaned);
        }
    }

    private static bool IsCleanCondition(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || BrokenFragmentRegex().IsMatch(value))
        {
            return false;
        }

        return KnownConditionRegex().IsMatch(value) || FamilyHistoryConditionRegex().IsMatch(value);
    }

    private static string CleanupClinicalTerm(string value)
    {
        var cleaned = value.Trim().TrimEnd('.', ',', ';', ':');
        cleaned = Regex.Replace(cleaned, @"^(?:some|mild|severe|ongoing|new|my|the|a|an)\s+", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        return cleaned;
    }

    private static string ExtractDateOfBirthFromContext(
        string transcript,
        IReadOnlyList<ConversationTurn>? conversationTurns)
    {
        var lines = BuildContextLines(transcript, conversationTurns);
        const int lookahead = 8;

        if (transcript.Contains("ծննդյան", StringComparison.OrdinalIgnoreCase))
        {
            var armenianTranscriptDob = AssembleArmenianDobFromText(transcript);
            if (!string.IsNullOrWhiteSpace(armenianTranscriptDob))
            {
                return armenianTranscriptDob;
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (!DobTriggerRegex().IsMatch(lines[i]))
            {
                continue;
            }

            var nearby = lines
                .Skip(i)
                .Take(lookahead + 1)
                .Select(RemoveSpeakerLabel)
                .ToList();

            var candidateText = string.Join(" ", nearby);
            var fullDate = ExtractFullDobDate(candidateText);
            if (!string.IsNullOrWhiteSpace(fullDate))
            {
                return fullDate;
            }

            var armenianDob = AssembleArmenianDobFromText(candidateText);
            if (!string.IsNullOrWhiteSpace(armenianDob))
            {
                return armenianDob;
            }

            var assembled = AssembleDobFromParts(nearby);
            if (!string.IsNullOrWhiteSpace(assembled))
            {
                return assembled;
            }
        }

        return string.Empty;
    }

    private static string AssembleArmenianDobFromText(string text)
    {
        if (!ArmenianMonthNameRegex().IsMatch(text))
        {
            return string.Empty;
        }

        int? day = null;
        var armenianWords = ArmenianWordRegex().Matches(text.ToLowerInvariant())
            .Select(match => NormalizeArmenianToken(match.Value))
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .ToArray();

        for (var i = 0; i < armenianWords.Length; i++)
        {
            var parsedDay = TryParseArmenianDayWords(armenianWords.Skip(i).Take(1));
            if (parsedDay is >= 1 and <= 31)
            {
                day = parsedDay;
                break;
            }
        }

        var year = ExtractArmenianYearFromText(text);

        return day is not null && year is not null
            ? $"March {day}, {year}"
            : string.Empty;
    }

    private static int? ExtractArmenianYearFromText(string text)
    {
        var nineteenHundreds = ArmenianNineteenHundredsYearRegex().Match(text);
        if (nineteenHundreds.Success)
        {
            var suffix = TryParseArmenianCardinalNumber(ArmenianWordRegex().Matches(nineteenHundreds.Groups["suffix"].Value)
                .Select(match => match.Value));
            return suffix is >= 0 and <= 99 ? 1900 + suffix.Value : null;
        }

        var twoThousands = ArmenianTwoThousandsYearRegex().Match(text);
        if (twoThousands.Success)
        {
            var suffix = TryParseArmenianCardinalNumber(ArmenianWordRegex().Matches(twoThousands.Groups["suffix"].Value)
                .Select(match => match.Value)) ?? 0;
            return suffix is >= 0 and <= 99 ? 2000 + suffix : null;
        }

        return null;
    }

    private static IReadOnlyList<string> BuildContextLines(
        string transcript,
        IReadOnlyList<ConversationTurn>? conversationTurns)
    {
        var lines = transcript
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (conversationTurns is not null && conversationTurns.Count > 0)
        {
            lines.AddRange(conversationTurns
                .Select(turn => $"{turn.Role}: {turn.Text}")
                .Where(line => !string.IsNullOrWhiteSpace(line)));
        }

        if (lines.Count <= 1)
        {
            lines.AddRange(SentenceBoundaryRegex().Split(transcript)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line)));
        }

        return lines
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ExtractFullDobDate(string text)
    {
        foreach (var regex in new[] { MonthDayYearRegex(), ArmenianMonthDayYearRegex(), DayMonthYearRegex(), NumericDateRegex(), IsoDateRegex() })
        {
            var match = regex.Match(text);
            if (match.Success && !IsRelativeDateNoise(match.Value))
            {
                return NormalizeDateCandidate(match.Value);
            }
        }

        return string.Empty;
    }

    private static string AssembleDobFromParts(IEnumerable<string> nearbyLines)
    {
        string? monthText = null;
        int? day = null;
        int? year = null;

        foreach (var rawLine in nearbyLines)
        {
            var line = RemoveSpeakerLabel(rawLine);
            if (monthText is null || day is null)
            {
                var monthDay = ExtractMonthAndDay(line);
                monthText ??= monthDay.Month;
                day ??= monthDay.Day;
            }

            monthText ??= ExtractMonthName(line);
            day ??= ExtractDay(line);
            year ??= ExtractYear(line);

            if (monthText is not null && day is not null && year is not null)
            {
                return $"{monthText} {day}, {year}";
            }
        }

        return string.Empty;
    }

    private static (string? Month, int? Day) ExtractMonthAndDay(string line)
    {
        var match = MonthDayPartialRegex().Match(line);
        if (!match.Success)
        {
            match = ArmenianMonthDayPartialRegex().Match(line);
            if (!match.Success)
            {
                return (null, null);
            }
        }

        var month = NormalizeMonth(match.Groups["month"].Value);
        var day = int.TryParse(match.Groups["day"].Value, out var numericDay) && numericDay is >= 1 and <= 31
            ? numericDay
            : (int?)null;

        return (month, day);
    }

    private static string RemoveSpeakerLabel(string value)
    {
        return Regex.Replace(value, @"^\s*(?:Agent|Caller|Speaker\s+\d+|Գործակալ|Զանգահարող|Հաճախորդ|Օպերատոր)\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
    }

    private static string NormalizeDateCandidate(string value)
    {
        var cleaned = value.Trim().TrimEnd('.', ',', ';');
        var monthDayYear = MonthDayYearRegex().Match(cleaned);
        if (!monthDayYear.Success)
        {
            monthDayYear = ArmenianMonthDayYearRegex().Match(cleaned);
        }

        if (monthDayYear.Success)
        {
            var month = NormalizeMonth(monthDayYear.Groups["month"].Value);
            var day = int.Parse(monthDayYear.Groups["day"].Value);
            var year = NormalizeTwoDigitYear(monthDayYear.Groups["year"].Value);
            return $"{month} {day}, {year}";
        }

        var dayMonthYear = DayMonthYearRegex().Match(cleaned);
        if (dayMonthYear.Success)
        {
            var month = NormalizeMonth(dayMonthYear.Groups["month"].Value);
            var day = int.Parse(dayMonthYear.Groups["day"].Value);
            var year = NormalizeTwoDigitYear(dayMonthYear.Groups["year"].Value);
            return $"{month} {day}, {year}";
        }

        return cleaned;
    }

    private static string? ExtractMonthName(string line)
    {
        var match = MonthNameRegex().Match(line);
        if (match.Success)
        {
            return NormalizeMonth(match.Value);
        }

        var armenianMatch = ArmenianMonthNameRegex().Match(line);
        return armenianMatch.Success ? NormalizeMonth(armenianMatch.Value) : null;
    }

    private static int? ExtractDay(string line)
    {
        var ordinal = DayOrdinalRegex().Match(line);
        if (ordinal.Success && int.TryParse(ordinal.Groups["day"].Value, out var numericDay))
        {
            return numericDay is >= 1 and <= 31 ? numericDay : null;
        }

        var words = Regex.Matches(line.ToLowerInvariant(), @"[a-z]+")
            .Select(match => match.Value)
            .ToArray();

        for (var i = 0; i < words.Length; i++)
        {
            var oneWord = TryParseOrdinalWords(words.Skip(i).Take(1));
            if (oneWord is >= 1 and <= 31)
            {
                return oneWord;
            }

            var twoWords = TryParseOrdinalWords(words.Skip(i).Take(2));
            if (twoWords is >= 1 and <= 31)
            {
                return twoWords;
            }
        }

        var armenianWords = ArmenianWordRegex().Matches(line.ToLowerInvariant())
            .Select(match => NormalizeArmenianToken(match.Value))
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .ToArray();

        for (var i = 0; i < armenianWords.Length; i++)
        {
            var oneWord = TryParseArmenianDayWords(armenianWords.Skip(i).Take(1));
            if (oneWord is >= 1 and <= 31)
            {
                return oneWord;
            }
        }

        return null;
    }

    private static int? ExtractYear(string line)
    {
        var numeric = YearRegex().Match(line);
        if (numeric.Success && int.TryParse(numeric.Value, out var year))
        {
            return year;
        }

        var words = Regex.Matches(line.ToLowerInvariant(), @"[a-z]+")
            .Select(match => match.Value)
            .ToArray();

        for (var i = 0; i < words.Length; i++)
        {
            for (var length = Math.Min(4, words.Length - i); length >= 1; length--)
            {
                var spokenYear = TryParseSpokenYear(words.Skip(i).Take(length).ToArray());
                if (spokenYear is >= 1900 and <= 2099)
                {
                    return spokenYear;
                }
            }
        }

        var armenianWords = ArmenianWordRegex().Matches(line.ToLowerInvariant())
            .Select(match => NormalizeArmenianToken(match.Value))
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .ToArray();

        for (var i = 0; i < armenianWords.Length; i++)
        {
            for (var length = Math.Min(6, armenianWords.Length - i); length >= 1; length--)
            {
                var spokenYear = TryParseArmenianSpokenYear(armenianWords.Skip(i).Take(length).ToArray());
                if (spokenYear is >= 1900 and <= 2099)
                {
                    return spokenYear;
                }
            }
        }

        return null;
    }

    private static int NormalizeTwoDigitYear(string year)
    {
        if (year.Length == 2 && int.TryParse(year, out var twoDigitYear))
        {
            return twoDigitYear >= 30 ? 1900 + twoDigitYear : 2000 + twoDigitYear;
        }

        return int.Parse(year);
    }

    private static string NormalizeMonth(string value)
    {
        var key = value[..3].ToLowerInvariant();
        var armenian = value.Trim().TrimEnd('ը', 'ն').ToLowerInvariant();
        if (armenian.StartsWith("մարտ", StringComparison.Ordinal))
        {
            return "March";
        }

        return key switch
        {
            "jan" => "January",
            "feb" => "February",
            "mar" => "March",
            "apr" => "April",
            "may" => "May",
            "jun" => "June",
            "jul" => "July",
            "aug" => "August",
            "sep" => "September",
            "oct" => "October",
            "nov" => "November",
            "dec" => "December",
            _ => value
        };
    }

    private static int? TryParseArmenianDayWords(IEnumerable<string> words)
    {
        var phrase = string.Join(' ', words.Select(NormalizeArmenianToken)).Trim();
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return null;
        }

        return TryParseArmenianCardinalNumber([phrase]);
    }

    private static int? TryParseArmenianSpokenYear(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return null;
        }

        var normalized = words.Select(NormalizeArmenianToken).Where(word => !string.IsNullOrWhiteSpace(word)).ToArray();
        var thousandIndex = Array.IndexOf(normalized, "հազար");
        if (thousandIndex < 0)
        {
            return null;
        }

        if (thousandIndex > 0)
        {
            var multiplier = TryParseArmenianCardinalNumber(normalized.Take(thousandIndex));
            if (multiplier is null)
            {
                return null;
            }

            var suffix = TryParseArmenianCardinalNumber(normalized.Skip(thousandIndex + 1)) ?? 0;
            return multiplier.Value * 1000 + suffix;
        }

        var remainder = normalized.Skip(thousandIndex + 1).ToArray();
        if (remainder.Length >= 2 && remainder[1] == "հարյուր")
        {
            if (remainder.Length <= 2)
            {
                return null;
            }

            var hundreds = TryParseArmenianCardinalNumber([remainder[0]]);
            if (hundreds is null)
            {
                return null;
            }

            var suffix = TryParseArmenianCardinalNumber(remainder.Skip(2)) ?? 0;
            return 1000 + hundreds.Value * 100 + suffix;
        }

        return null;
    }

    private static int? TryParseArmenianCardinalNumber(IEnumerable<string> words)
    {
        var wordList = words.Select(NormalizeArmenianToken).Where(word => !string.IsNullOrWhiteSpace(word)).ToArray();
        var phrase = string.Join(' ', wordList).Trim();
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return 0;
        }

        int? direct = phrase switch
        {
            "մեկ" or "առաջին" => 1,
            "երկու" or "երկրորդ" => 2,
            "երեք" or "երրորդ" => 3,
            "չորս" or "չորրորդ" => 4,
            "հինգ" or "հինգերորդ" => 5,
            "վեց" or "վեցերորդ" => 6,
            "յոթ" or "յոթերորդ" => 7,
            "ութ" or "ութերորդ" => 8,
            "ինը" or "ինն" or "իններորդ" => 9,
            "տաս" or "տասներորդ" => 10,
            "տասնմեկ" => 11,
            "տասներկու" => 12,
            "տասներեք" => 13,
            "տասնչորս" => 14,
            "տասնհինգ" => 15,
            "տասնվեց" => 16,
            "տասնյոթ" => 17,
            "տասնութ" => 18,
            "տասնինը" => 19,
            "քսան" => 20,
            "քսանմեկ" => 21,
            "քսաներկու" => 22,
            "քսաներեք" => 23,
            "քսանչորս" => 24,
            "քսանհինգ" => 25,
            "քսանվեց" => 26,
            "քսանյոթ" => 27,
            "քսանութ" => 28,
            "քսանինը" => 29,
            "երեսուն" => 30,
            "երեսունմեկ" => 31,
            "ութսուն" => 80,
            "ութսունմեկ" => 81,
            "ութսուներկու" => 82,
            "ութսուներեք" => 83,
            "ութսունչորս" => 84,
            "ութսունհինգ" => 85,
            "ութսունվեց" => 86,
            "ութսունյոթ" => 87,
            "ութսունութ" => 88,
            "ութսունինը" => 89,
            "իննսուն" => 90,
            "իննսունմեկ" => 91,
            "իննսուներկու" => 92,
            "իննսուներեք" => 93,
            "իննսունչորս" => 94,
            "իննսունհինգ" => 95,
            "իննսունվեց" => 96,
            "իննսունյոթ" => 97,
            "իննսունութ" => 98,
            "իննսունինը" => 99,
            _ => null
        };

        if (direct is not null)
        {
            return direct;
        }

        if (wordList.Length != 2)
        {
            return null;
        }

        var tens = wordList[0] switch
        {
            "քսան" => 20,
            "երեսուն" => 30,
            "ութսուն" => 80,
            "իննսուն" => 90,
            _ => 0
        };

        var ones = TryParseArmenianCardinalNumber([wordList[1]]);
        return tens > 0 && ones is >= 1 and <= 9 ? tens + ones.Value : null;
    }

    private static string NormalizeArmenianToken(string value)
    {
        var token = value.Trim().Trim('։', ':', ',', '.', '?', '՞', '՛');
        if (token == "ինը")
        {
            return token;
        }

        token = token.Trim('ը');
        return token == "ու" ? "" : token;
    }

    private static int? TryParseOrdinalWords(IEnumerable<string> words)
    {
        var phrase = string.Join(' ', words).Trim();
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return null;
        }

        return phrase switch
        {
            "first" => 1,
            "second" => 2,
            "third" => 3,
            "fourth" => 4,
            "fifth" => 5,
            "sixth" => 6,
            "seventh" => 7,
            "eighth" => 8,
            "ninth" => 9,
            "tenth" => 10,
            "eleventh" => 11,
            "twelfth" => 12,
            "thirteenth" => 13,
            "fourteenth" => 14,
            "fifteenth" => 15,
            "sixteenth" => 16,
            "seventeenth" => 17,
            "eighteenth" => 18,
            "nineteenth" => 19,
            "twentieth" => 20,
            "twenty first" => 21,
            "twenty second" => 22,
            "twenty third" => 23,
            "twenty fourth" => 24,
            "twenty fifth" => 25,
            "twenty sixth" => 26,
            "twenty seventh" => 27,
            "twenty eighth" => 28,
            "twenty ninth" => 29,
            "thirtieth" => 30,
            "thirty first" => 31,
            _ => null
        };
    }

    private static int? TryParseSpokenYear(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return null;
        }

        var phrase = string.Join(' ', words);
        if (phrase.Contains("nineteen", StringComparison.OrdinalIgnoreCase))
        {
            var index = Array.IndexOf(words.ToArray(), "nineteen");
            var suffix = TryParseCardinalNumber(words.Skip(index + 1));
            return suffix is >= 0 and <= 99 ? 1900 + suffix.Value : null;
        }

        if (phrase.Contains("two thousand", StringComparison.OrdinalIgnoreCase))
        {
            var index = Array.IndexOf(words.ToArray(), "thousand");
            var suffix = TryParseCardinalNumber(words.Skip(index + 1)) ?? 0;
            return 2000 + suffix;
        }

        var twentyIndex = Array.IndexOf(words.ToArray(), "twenty");
        if (twentyIndex >= 0)
        {
            var suffixWords = words.Skip(twentyIndex + 1).ToArray();
            var suffix = TryParseCardinalNumber(suffixWords) ?? 0;

            if (suffixWords.Length > 0 && suffixWords[0] == "twenty")
            {
                return suffix is >= 20 and <= 99 ? 2000 + suffix : null;
            }

            return suffix is >= 0 and <= 9 ? 2020 + suffix : null;
        }

        return null;
    }

    private static int? TryParseCardinalNumber(IEnumerable<string> words)
    {
        var wordList = words.ToArray();
        var phrase = string.Join(' ', wordList).Trim();
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return 0;
        }

        int? direct = phrase switch
        {
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            "eleven" => 11,
            "twelve" => 12,
            "thirteen" => 13,
            "fourteen" => 14,
            "fifteen" => 15,
            "sixteen" => 16,
            "seventeen" => 17,
            "eighteen" => 18,
            "nineteen" => 19,
            "twenty" => 20,
            "thirty" => 30,
            "forty" => 40,
            "fifty" => 50,
            "sixty" => 60,
            "seventy" => 70,
            "eighty" => 80,
            "ninety" => 90,
            _ => null
        };

        if (direct is not null)
        {
            return direct;
        }

        if (wordList.Length != 2)
        {
            return null;
        }

        var tens = wordList[0] switch
        {
            "twenty" => 20,
            "thirty" => 30,
            "forty" => 40,
            "fifty" => 50,
            "sixty" => 60,
            "seventy" => 70,
            "eighty" => 80,
            "ninety" => 90,
            _ => 0
        };

        var ones = wordList[1] switch
        {
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            _ => 0
        };

        return tens > 0 && ones > 0 ? tens + ones : null;
    }

    private static bool IsRelativeDateNoise(string value)
    {
        return RelativeDateNoiseRegex().IsMatch(value);
    }

    private static string CleanSsn(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (ArmenianIdRegex().IsMatch(trimmed))
        {
            return ArmenianIdRegex().Match(trimmed).Value;
        }

        if (GenericIdentifierValueRegex().IsMatch(trimmed) && Regex.IsMatch(trimmed, @"[A-Za-z]"))
        {
            return trimmed;
        }

        var digits = NonDigitRegex().Replace(trimmed, "");
        return digits.Length switch
        {
            9 => $"{digits[..3]}-{digits[3..5]}-{digits[5..]}",
            4 => $"***-**-{digits}",
            >= 5 and <= 20 => trimmed,
            _ => string.Empty
        };
    }

    private static string CleanPhone(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var digits = NonDigitRegex().Replace(value, "");
        if (value.TrimStart().StartsWith("+374", StringComparison.Ordinal) && digits.Length == 11)
        {
            return $"+374 {digits[3..5]} {digits[5..]}";
        }

        if (digits.Length == 11 && digits.StartsWith('1'))
        {
            digits = digits[1..];
        }

        return digits.Length == 10
            ? $"{digits[..3]}-{digits[3..6]}-{digits[6..]}"
            : string.Empty;
    }

    private static string CleanEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var match = EmailRegex().Match(value);
        return match.Success ? match.Value : string.Empty;
    }

    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\+?1[\s\-.]?\(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4}|\(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4}|\+374[\s\-.]?\d{2}[\s\-.]?\d{6}|0\d{2}[\s\-.]?\d{6}")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b\d{3}[\s\-]\d{2}[\s\-]\d{4}\b")]
    private static partial Regex SsnRegex();

    [GeneratedRegex(@"\b[A-Z]{2}\d{7}\b")]
    private static partial Regex ArmenianIdRegex();

    [GeneratedRegex(@"\b(?:insurance|policy|member|patient|case|national)\s*(?:number|no\.?|#|id)?\s*(?:is|:|#)?\s*(?<value>[A-Z0-9][A-Z0-9\-]{3,30})\b", RegexOptions.IgnoreCase)]
    private static partial Regex GenericIdRegex();

    [GeneratedRegex(@"\b(?:սոցիալական ապահովության համար|նույնականացման համար|ազգային\s*id|անձնական համար)\s*(?:է|:|#)?\s*(?<value>[A-Z0-9][A-Z0-9\-]{3,30}|\d{3}[\s\-]\d{2}[\s\-]\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArmenianGenericIdRegex();

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9\-]{3,30}$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericIdentifierValueRegex();

    [GeneratedRegex(@"(?:my name is|անունը|անունս|իմ անունը)\s+([A-ZԱ-Ֆա-ֆ\w][a-zA-Zա-ֆԱ-Ֆ\w]+(?:\s+[A-ZԱ-Ֆա-ֆ\w]\.?)?(?:\s+[A-ZԱ-Ֆա-ֆ\w][a-zA-Zա-ֆԱ-Ֆ\w]+){0,3})", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"\b(?:my name is|name is|it's|it is|this is|i am|i'm|written as|spelled as|go by|անունս|իմ անունը|անունը)\s+(?<value>[^.\n։]*(?:[.։][^.\n։]*){0,2})", RegexOptions.IgnoreCase)]
    private static partial Regex NameContextRegex();

    [GeneratedRegex(@"(?:\b(?:full name|your name|confirm name|confirm your name|can i have your name|who am i speaking with)\b|ամբողջական անուն|ձեր անուն|հաստատել անունը|հաստատել ձեր անունը|ում հետ եմ խոսում)", RegexOptions.IgnoreCase)]
    private static partial Regex NamePromptRegex();

    [GeneratedRegex(@"(?:[A-Z][a-z]+|[Ա-Ֆ][ա-ֆ]+)(?:\s+(?:[A-Z]\.|[Ա-Ֆ]\.?))?(?:\s+(?:[A-Z][a-z]+|[Ա-Ֆ][ա-ֆ]+)){1,3}")]
    private static partial Regex CandidateNameRegex();

    [GeneratedRegex(@"(?:i live at|my address is|located at|address[:\s]+|ես ապրում եմ)\s*(.+?)(?:\.|։|,\s*[A-Z]{2}\s*\d{5}|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AddressRegex();

    [GeneratedRegex(@"\b(?:confirm\s+(?:your\s+)?address|current address|home address|your address|where do you live|where are you living|mailing address|residential address|i live at|my address is|հաստատել ձեր հասցեն|ձեր հասցեն|ձեր ներկայիս հասցեն|ներկայիս հասցեն|բնակության հասցեն|որտե՞ղ եք ապրում|որտեղ եք ապրում|իմ հասցեն)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AddressPromptRegex();

    [GeneratedRegex(@"\b(?:pharmacy|drugstore|drug mart|prescription pickup|preferred pharmacy|send it to|fax it to|դեղատուն|դեղատնից|նախընտրած դեղատուն|դեղը վերցնել)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PharmacyContextRegex();

    [GeneratedRegex(@"\b\d{1,6}\s+[A-ZԱ-Ֆ][A-Za-zԱ-Ֆա-ֆ0-9.'\-]*(?:\s+[A-ZԱ-Ֆա-ֆ]?[A-Za-zԱ-Ֆա-ֆ0-9.'\-]+){1,12}(?:,\s*(?:Apartment|Apt\.?|Unit|Suite|բնակարան)\s*[A-Za-z0-9\-]+)?(?:,\s*[A-ZԱ-Ֆ][A-Za-zԱ-Ֆա-ֆ.'\-]+){0,4}(?:,\s*[A-Z]\d[A-Z]\s?\d[A-Z]\d)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex AddressCandidateRegex();

    [GeneratedRegex(@"\b[A-Z]\d[A-Z]\s?\d[A-Z]\d\b", RegexOptions.IgnoreCase)]
    private static partial Regex PostalCodeRegex();

    [GeneratedRegex(@"(?:\b(?:date of birth|dob|born(?:\s+on)?|birthdate|birth date)\b|ծննդյան ամսաթիվ|ծննդյան օրը|ծննդյան թիվ|ծննդյան տարեթիվ)\s*(?:is|է|:)?\s*(?<date>(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?|մարտ(?:ի|ն|ը)?)\s+\d{1,2},?\s+\d{4}|\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{4}[/-]\d{1,2}[/-]\d{1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex DateOfBirthRegex();

    [GeneratedRegex(@"(?:\b(?:date of birth|dob|birth date|birthdate|born|born on|when were you born)\b|ծննդյան ամսաթիվ|ծննդյան օրը|ծնվել եք|ծնվել եք ե՞րբ|ծննդյան տարեթիվ|ծննդյան թիվ)", RegexOptions.IgnoreCase)]
    private static partial Regex DobTriggerRegex();

    [GeneratedRegex(@"\b(?<month>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s+(?<year>\d{2,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthDayYearRegex();

    [GeneratedRegex(@"\b(?<month>մարտ(?:ի|ն|ը)?)\s+(?<day>\d{1,2})(?:,)?\s+(?<year>\d{2,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArmenianMonthDayYearRegex();

    [GeneratedRegex(@"\b(?<month>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s+(?<day>\d{1,2})(?:st|nd|rd|th)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthDayPartialRegex();

    [GeneratedRegex(@"\b(?<month>մարտ(?:ի|ն|ը)?)\s+(?<day>\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArmenianMonthDayPartialRegex();

    [GeneratedRegex(@"\b(?<day>\d{1,2})(?:st|nd|rd|th)?\s+(?<month>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s+(?<year>\d{2,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex DayMonthYearRegex();

    [GeneratedRegex(@"\b\d{1,2}[/-]\d{1,2}[/-]\d{2,4}\b")]
    private static partial Regex NumericDateRegex();

    [GeneratedRegex(@"\b\d{4}[/-]\d{1,2}[/-]\d{1,2}\b")]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"\b(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthNameRegex();

    [GeneratedRegex(@"մարտ(?:ի|ն|ը)?", RegexOptions.IgnoreCase)]
    private static partial Regex ArmenianMonthNameRegex();

    [GeneratedRegex(@"\b(?<day>\d{1,2})(?:st|nd|rd|th)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DayOrdinalRegex();

    [GeneratedRegex(@"\b(?:19|20)\d{2}\b")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\b(?:today|now|tomorrow|yesterday|next\s+\w+|morning|afternoon|evening)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeDateNoiseRegex();

    [GeneratedRegex(@"(?<=[.!?։])\s+")]
    private static partial Regex SentenceBoundaryRegex();

    [GeneratedRegex(@"\bDr\.?\s+[A-Z][a-z]+(?:\s+[A-Z][a-z]+){0,3}\b")]
    private static partial Regex DoctorRegex();

    [GeneratedRegex(@"\b(?:դոկտոր|բժիշկ)\s+[Ա-Ֆ][ա-ֆ]+(?:\s+[Ա-Ֆ][ա-ֆ]+){0,3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArmenianDoctorRegex();

    [GeneratedRegex(@"\b(?:doctor|provider|Dr\.?|appointment with Dr\.?|preferred doctor|բժիշկ|դոկտոր|մասնագետ|նախընտրած բժիշկ|հանդիպում բժշկի հետ)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DoctorPromptRegex();

    [GeneratedRegex(@"\b(?:medications?|meds?|taking|takes|take|prescribed|on|դեղեր?|ընդունում եմ|նշանակել է|վերալիցքավորում|դոզա)\s*(?:are|is|include|includes|:|է)?\s*(?<items>[A-Za-zԱ-Ֆա-ֆ][A-Za-zԱ-Ֆա-ֆ0-9\s\-\/]+?)(?:\.|։|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MedicationContextRegex();

    [GeneratedRegex(@"\b(?:Metformin|Vitamin\s+D|Omeprazole|Մետֆորմին|Վիտամին\s+D|Օմեպրազոլ)\b", RegexOptions.IgnoreCase)]
    private static partial Regex KnownMedicationRegex();

    [GeneratedRegex(@"\b(?:Metformin|Vitamin\s+D|Omeprazole|Մետֆորմին|Վիտամին\s+D|Օմեպրազոլ)\s*(?:\d+(?:\.\d+)?\s*(?:mg|մգ|mcg|g|IU|units?))?(?:\s+(?:once|twice|three times|four times)\s+daily|\s+in the morning|\s+at night|\s+with meals|\s+օրը\s+(?:մեկ|երկու|երեք|չորս)\s+անգամ|\s+առավոտյան|\s+երեկոյան)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex MedicationMentionRegex();

    [GeneratedRegex(@"\b(?:medications?|meds?|taking|takes|take|prescribed|refill(?:ing)?|on|դեղեր?|ընդունում եմ|նշանակել է|վերալիցքավորում|դոզա|մգ|առավոտյան|երեկոյան)\b(?<items>[^.\n։]{0,220})", RegexOptions.IgnoreCase)]
    private static partial Regex MedicationContextLineRegex();

    [GeneratedRegex(@"\b(?:symptoms?|complains? of|reports?|experiencing|has|have|diagnosed with|history of|ախտանիշներ|գանգատվում է|ունեմ|ունի|ախտորոշվել է)\s*(?:are|is|include|includes|:|է)?\s*(?<items>[A-Za-zԱ-Ֆա-ֆ][A-Za-zԱ-Ֆա-ֆ\s\-]+?)(?:\.|։|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ConditionContextRegex();

    [GeneratedRegex(@"\b(?:chest tightness|dizziness|fatigue|fatty liver|insulin resistance|nausea|anxiety|abdominal discomfort|heartburn|reflux|կրծքավանդակի սեղմվածություն|գլխապտույտ|հոգնածություն|լյարդի ճարպակալում|ինսուլինային ռեզիստենտություն|սրտխառնոց|անհանգստություն|որովայնի անհարմարություն|այրոց|ռեֆլյուքս)\b", RegexOptions.IgnoreCase)]
    private static partial Regex KnownConditionRegex();

    [GeneratedRegex(@"\b(?:family history of liver problems|ընտանեկան պատմություն լյարդի խնդիրներով)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FamilyHistoryConditionRegex();

    [GeneratedRegex(@"^(?:[a-z]{1,2}\s+)?(?:e second|tario|ical care|cation|care|checkout|ly general confirmation|e is the best one now|few different things to ask about|maybe five tablets left|lab results|need review|it available|մի քանի տարբեր հարց ունեմ|հինգ հաբ ունեմ մնացած|լաբորատոր արդյունքներ|պետք է վերանայում)$", RegexOptions.IgnoreCase)]
    private static partial Regex BrokenFragmentRegex();

    [GeneratedRegex(@"[ա-ֆև]+", RegexOptions.IgnoreCase)]
    private static partial Regex ArmenianWordRegex();

    [GeneratedRegex(@"հազար\s+ին(?:ը|ն)\s+հարյուր\s+(?<suffix>[ա-ֆև]+(?:\s+[ա-ֆև]+)?)(?=\s*[։:,.?\n]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ArmenianNineteenHundredsYearRegex();

    [GeneratedRegex(@"երկու\s+հազար(?:\s+(?<suffix>[ա-ֆև]+(?:\s+[ա-ֆև]+)?))?(?=\s*[։:,.?\n]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ArmenianTwoThousandsYearRegex();

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigitRegex();
}
