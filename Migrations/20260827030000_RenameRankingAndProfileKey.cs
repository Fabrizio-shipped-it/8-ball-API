using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PoolManager.Migrations
{
    /// <inheritdoc />
    public partial class RenameRankingAndProfileKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Ranking" guardaba en realidad la cantidad de victorias, no una posición.
            // La posición ahora se calcula en el servicio a partir de este contador.
            migrationBuilder.RenameColumn(
                name: "Ranking",
                table: "Players",
                newName: "Wins");

            // Se guarda la key de S3, no la URL completa.
            migrationBuilder.RenameColumn(
                name: "ProfilePictureUrl",
                table: "Players",
                newName: "ProfilePictureKey");

            // Un jugador recién registrado no tiene foto: la columna pasa a ser opcional.
            migrationBuilder.AlterColumn<string>(
                name: "ProfilePictureKey",
                table: "Players",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            // Los valores existentes eran URLs completas y "pending", y sus keys
            // apuntaban a carpetas con GUID aleatorio que ya no pasan el chequeo
            // de pertenencia. Se limpian: hay que volver a subir la foto.
            migrationBuilder.Sql(@"UPDATE ""Players"" SET ""ProfilePictureKey"" = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"UPDATE ""Players"" SET ""ProfilePictureKey"" = '' WHERE ""ProfilePictureKey"" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "ProfilePictureKey",
                table: "Players",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.RenameColumn(
                name: "ProfilePictureKey",
                table: "Players",
                newName: "ProfilePictureUrl");

            migrationBuilder.RenameColumn(
                name: "Wins",
                table: "Players",
                newName: "Ranking");
        }
    }
}
