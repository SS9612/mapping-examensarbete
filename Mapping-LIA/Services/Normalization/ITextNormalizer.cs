namespace Mapping_LIA.Services.Normalization;

public interface ITextNormalizer
{
    string Normalize(string input);

    string NormalizeForSearch(string input);
}