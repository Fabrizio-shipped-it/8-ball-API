using Microsoft.EntityFrameworkCore;
using PoolManager.Models;

namespace PoolManager.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    /// Construccion de tablas.
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Match> Matches => Set<Match>();

    /// Propiedades de cada tabla
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Player config
        modelBuilder.Entity<Player>(entity =>
        {
            entity.HasIndex(p => p.KeycloakId).IsUnique();
            entity.Property(p => p.KeycloakId).IsRequired();
            entity.Property(p => p.Name).IsRequired();
            entity.Property(p => p.ProfilePictureUrl).IsRequired();
        });

        // Match config
        modelBuilder.Entity<Match>(entity =>
        {
            entity.HasIndex(m => m.StartTime);      /// Index en este campo para que las busquedas por fecha sean mas eficientes.
            
            entity.Property(m => m.StartTime).IsRequired();

            entity.HasOne(m => m.Player1)               /// HasOne/WithMany: define las relaciones. Un Match tiene un Player1
                  .WithMany(p => p.MatchesAsPlayer1)    /// y un Player tiene muchos MatchesAsPlayer1
                  .HasForeignKey(m => m.Player1Id)
                  .OnDelete(DeleteBehavior.Restrict);   /// Al borrar un Player, no borra sus partidas en cascada (tira error, y evita perder data).

            entity.HasOne(m => m.Player2)
                  .WithMany(p => p.MatchesAsPlayer2)
                  .HasForeignKey(m => m.Player2Id)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(m => m.Winner)
                  .WithMany()
                  .HasForeignKey(m => m.WinnerId)
                  .OnDelete(DeleteBehavior.SetNull);/// Al borrar un Player, no borra sus partidas en cascada (tira error, y evita perder data).
        });
    }
}