namespace Mapping_LIA.Services.AreaMapper;

public interface IAreaMapperService
{
    Task<MapResult> MapCompetenceAsync(string competence, CancellationToken ct = default);
    Task<BatchMapResult> MapCompetencesAsync(IEnumerable<string> competences, CancellationToken ct = default);
}
// Records for mapping results
public record MapResult(
    bool Success,
    string? ErrorMessage,
    MapResponse? Response
);
// Record for batch mapping results
public record BatchMapResult(
    IReadOnlyList<MapResponse> Results,
    IReadOnlyList<string> Errors
);
// Record for individual mapping response
public record MapResponse(
    string Input,
    string Normalized,
    string Area,
    double Confidence
);
