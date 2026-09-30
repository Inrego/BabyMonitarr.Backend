namespace BabyMonitarr.Backend.Services;

/// <summary>
/// Google's SDP answers (SDM and Google Home alike) carry ICE candidates SIPSorcery's parser
/// cannot read. They are stripped from the answer and added one by one after normalizing.
/// </summary>
public static class NestSdp
{
    /// <summary>
    /// The answer without its candidate lines, plus each candidate (without the <c>a=</c>
    /// prefix) with the mid and m-line index it belongs to.
    /// </summary>
    public static (string CleanedSdp, List<(string Candidate, string? Mid, ushort MLineIndex)> Candidates) SplitCandidates(string sdp)
    {
        var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var candidates = new List<(string, string?, ushort)>();
        string? mid = null;
        int mLineIndex = -1;

        foreach (var line in lines)
        {
            if (line.StartsWith("m=")) mLineIndex++;
            else if (line.StartsWith("a=mid:")) mid = line.Substring(6);
            else if (line.StartsWith("a=candidate:")) candidates.Add((line.Substring(2), mid, (ushort)Math.Max(mLineIndex, 0)));
        }

        return (string.Join("\r\n", lines.Where(l => !l.StartsWith("a=candidate:"))), candidates);
    }

    public static bool TryNormalizeIceCandidate(
        string candidate,
        out string normalizedCandidate,
        out string reason)
    {
        normalizedCandidate = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            reason = "empty candidate";
            return false;
        }

        var candidateText = candidate.Trim();
        if (candidateText.StartsWith("a=", StringComparison.OrdinalIgnoreCase))
        {
            candidateText = candidateText.Substring(2).Trim();
        }

        if (!candidateText.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
        {
            reason = "missing candidate: prefix";
            return false;
        }

        var candidateBody = candidateText.Substring("candidate:".Length).TrimStart();
        var fields = candidateBody.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 7)
        {
            reason = $"expected at least 7 candidate fields but got {fields.Length}";
            return false;
        }

        // Nest SDP answers can omit the foundation and start at component-id:
        // candidate: 1 udp 2113939711 74.125.247.232 19305 typ host ...
        // SIPSorcery expects a normalized token stream with foundation present.
        var hasFoundation = fields.Length >= 8 &&
            ushort.TryParse(fields[1], out _) &&
            IsIceTransportToken(fields[2]);
        var missingFoundation = ushort.TryParse(fields[0], out _) &&
            IsIceTransportToken(fields[1]);

        if (!hasFoundation && !missingFoundation)
        {
            reason = "unrecognized candidate token layout";
            return false;
        }

        var tokens = new List<string>(fields.Length + 1);
        if (hasFoundation)
        {
            if (string.IsNullOrWhiteSpace(fields[0]))
            {
                reason = "candidate foundation missing";
                return false;
            }

            tokens.Add($"candidate:{fields[0]}");
            for (var i = 1; i < fields.Length; i++)
            {
                tokens.Add(fields[i]);
            }
        }
        else
        {
            // Synthesize a deterministic foundation from priority to satisfy parser.
            tokens.Add($"candidate:nest{fields[2]}");
            for (var i = 0; i < fields.Length; i++)
            {
                tokens.Add(fields[i]);
            }
        }

        if (tokens.Count < 8)
        {
            reason = $"expected at least 8 normalized tokens but got {tokens.Count}";
            return false;
        }
        if (!ushort.TryParse(tokens[1], out _))
        {
            reason = $"invalid component token '{tokens[1]}'";
            return false;
        }
        if (!IsIceTransportToken(tokens[2]))
        {
            reason = $"invalid transport token '{tokens[2]}'";
            return false;
        }
        if (!ushort.TryParse(tokens[5], out _))
        {
            reason = $"invalid port token '{tokens[5]}'";
            return false;
        }
        if (!tokens[6].Equals("typ", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"expected 'typ' token at position 7 but found '{tokens[6]}'";
            return false;
        }

        if (tokens[2].Equals("ssltcp", StringComparison.OrdinalIgnoreCase))
        {
            tokens[2] = "tcp";
        }
        else
        {
            tokens[2] = tokens[2].ToLowerInvariant(); // Protocol token expected by SIPSorcery parser.
        }

        tokens[6] = tokens[6].ToLowerInvariant(); // "typ" key.
        tokens[7] = tokens[7].ToLowerInvariant(); // Candidate type value.
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Equals("tcptype", StringComparison.OrdinalIgnoreCase))
            {
                tokens[i] = "tcpType"; // SIPSorcery parser expects camel-case key.
            }
            else if (tokens[i].Equals("raddr", StringComparison.OrdinalIgnoreCase))
            {
                tokens[i] = "raddr";
            }
            else if (tokens[i].Equals("rport", StringComparison.OrdinalIgnoreCase))
            {
                tokens[i] = "rport";
            }
        }

        normalizedCandidate = string.Join(" ", tokens);
        return true;
    }

    private static bool IsIceTransportToken(string token)
    {
        return token.Equals("udp", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("tcp", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("ssltcp", StringComparison.OrdinalIgnoreCase);
    }
}
