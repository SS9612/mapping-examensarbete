using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mapping_LIA.Entities;

[Table("Areas")]
public class Area
{
    [Key]
    public Guid AreaId { get; set; }

    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    public virtual ICollection<Category> Categories { get; set; } = new List<Category>();
}
