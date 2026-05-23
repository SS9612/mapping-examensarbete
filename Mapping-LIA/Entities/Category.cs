using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mapping_LIA.Entities;

[Table("Categories")]
public class Category
{
    [Key]
    public Guid CategoryId { get; set; }

    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public Guid AreaId { get; set; }

    [ForeignKey("AreaId")]
    public virtual Area Area { get; set; } = null!;

    public virtual ICollection<Subcategory> Subcategories { get; set; } = new List<Subcategory>();
}
