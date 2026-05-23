namespace Mapping_LIA.Entities
{
    public class AreaMapping
    {
        public Guid Id { get; set; }
        public string Input { get; set; } = "";
        public string Normalized { get; set; } = "";
        public Guid? AreaId { get; set; }
        public double Confidence { get; set; }
        public string Method { get; set; } = "llm";
        public string? Rationale { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}