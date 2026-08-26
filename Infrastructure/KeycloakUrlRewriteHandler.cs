namespace PoolManager.Infrastructure;

/// <summary>
/// Reescribe URLs de Keycloak en las llamadas internas del middleware JWT.
/// Cuando Keycloak devuelve un discovery document con URLs públicas (Elastic IP),
/// este handler las reemplaza por la IP privada para que el tráfico
/// se resuelva dentro de la VPC sin salir a internet.
/// </summary>
public class KeycloakUrlRewriteHandler : DelegatingHandler
{
    private readonly string _publicUrl;
    private readonly string _internalUrl;

    public KeycloakUrlRewriteHandler(string publicUrl, string internalUrl)
        : base(new HttpClientHandler())
    {
        _publicUrl = publicUrl.TrimEnd('/');
        _internalUrl = internalUrl.TrimEnd('/');
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri?.ToString();
        if (uri != null && uri.StartsWith(_publicUrl, StringComparison.OrdinalIgnoreCase))
        {
            request.RequestUri = new Uri(uri.Replace(_publicUrl, _internalUrl));
        }

        return base.SendAsync(request, cancellationToken);
    }
}
