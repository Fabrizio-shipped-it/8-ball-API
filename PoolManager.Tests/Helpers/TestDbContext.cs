using Microsoft.EntityFrameworkCore;
using PoolManager.Data;

namespace PoolManager.Tests.Helpers;

public static class TestDbContext
{
    public static AppDbContext Create()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    /// Repositorio real sobre la base en memoria.
    ///
    /// Los servicios ya no reciben un AppDbContext sino un IRepositorioDatos, así
    /// que los tests arman el repositorio sobre el contexto de prueba. Sigue siendo
    /// la implementación de verdad —no un doble— para que las consultas se ejerciten.
    public static IRepositorioDatos Repositorio(AppDbContext context) =>
        new RepositorioEfCore(context);
}
