namespace MediaHub.Core.Entities;

/// <summary>Utilisateur de l'application (toi = Admin, tes amis = Friend).</summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Friend;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<MediaRequest> Requests { get; set; } = new List<MediaRequest>();
}
