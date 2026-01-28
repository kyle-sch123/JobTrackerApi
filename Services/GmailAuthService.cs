using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;
using System.Text.Json;
using JobTrackerApi.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace JobTrackerApi.Services
{
    public class GmailAuthService
    {
        private readonly IMongoCollection<UserEmailConnection> _connectionCollection;
        private readonly IConfiguration _configuration;
        private readonly ILogger<GmailAuthService> _logger;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _redirectUri;

        public GmailAuthService(
            IOptions<JobApplicationDatabaseSettings> dbSettings,
            IConfiguration configuration,
            ILogger<GmailAuthService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            var settings = dbSettings.Value;
            var mongoClient = new MongoClient(settings.ConnectionString);
            var mongoDatabase = mongoClient.GetDatabase(settings.DatabaseName);
            _connectionCollection = mongoDatabase.GetCollection<UserEmailConnection>(
                settings.UserEmailConnectionCollectionName
            );

            _clientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID") ?? "";
            _clientSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET") ?? "";

            var isDevelopment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
            _redirectUri = isDevelopment
                ? Environment.GetEnvironmentVariable("GOOGLE_REDIRECT_URI_DEV") ?? ""
                : Environment.GetEnvironmentVariable("GOOGLE_REDIRECT_URI_PROD") ?? "";
        }

        // Generate OAuth authorization URL
        public string GetAuthorizationUrl(string userId, string state)
        {
            var scopes = new[] { "https://www.googleapis.com/auth/gmail.readonly" };
            // Encode userId into the state so it is available on callback without requiring frontend to pass it
            var stateObj = new { userId = userId ?? "", state = state ?? "" };
            var stateJson = JsonSerializer.Serialize(stateObj);
            var stateBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(stateJson));

            var authUrl = $"https://accounts.google.com/o/oauth2/v2/auth?" +
                $"client_id={Uri.EscapeDataString(_clientId)}&" +
                $"redirect_uri={Uri.EscapeDataString(_redirectUri)}&" +
                $"response_type=code&" +
                $"scope={Uri.EscapeDataString(string.Join(" ", scopes))}&" +
                $"access_type=offline&" +
                $"prompt=consent&" +
                $"state={Uri.EscapeDataString(stateBase64)}";

            return authUrl;
        }

        // Exchange authorization code for tokens
        public async Task<UserEmailConnection> ExchangeCodeForTokensAsync(string code, string userId)
        {
            try
            {
                var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = new ClientSecrets
                    {
                        ClientId = _clientId,
                        ClientSecret = _clientSecret
                    },
                    Scopes = new[] { "https://www.googleapis.com/auth/gmail.readonly" }
                });

                var tokenResponse = await flow.ExchangeCodeForTokenAsync(
                    userId,
                    code,
                    _redirectUri,
                    CancellationToken.None
                );

                // Get user's Gmail address
                var userEmail = await GetUserEmailFromToken(tokenResponse.AccessToken);

                // Check if connection already exists
                var existingConnection = await _connectionCollection
                    .Find(c => c.UserId == userId)
                    .FirstOrDefaultAsync();

                var connection = new UserEmailConnection
                {
                    Id = existingConnection?.Id,
                    UserId = userId,
                    Email = userEmail,
                    AccessToken = tokenResponse.AccessToken,
                    RefreshToken = tokenResponse.RefreshToken ?? existingConnection?.RefreshToken ?? "",
                    TokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresInSeconds ?? 3600),
                    Scopes = new List<string> { "https://www.googleapis.com/auth/gmail.readonly" },
                    IsActive = true,
                    ConnectedAt = existingConnection?.ConnectedAt ?? DateTime.UtcNow
                };

                if (existingConnection != null)
                {
                    await _connectionCollection.ReplaceOneAsync(c => c.Id == connection.Id, connection);
                }
                else
                {
                    await _connectionCollection.InsertOneAsync(connection);
                }

                _logger.LogInformation($"Gmail connected for user {userId}");
                return connection;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to exchange code for tokens for user {userId}");
                throw;
            }
        }

        // Refresh access token using refresh token
        public async Task<string> RefreshAccessTokenAsync(string userId)
        {
            var connection = await _connectionCollection
                .Find(c => c.UserId == userId && c.IsActive)
                .FirstOrDefaultAsync();

            if (connection == null)
            {
                throw new Exception("No active Gmail connection found");
            }

            // Check if token is still valid (with 5 minute buffer)
            if (connection.TokenExpiry > DateTime.UtcNow.AddMinutes(5))
            {
                return connection.AccessToken;
            }

            try
            {
                var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = new ClientSecrets
                    {
                        ClientId = _clientId,
                        ClientSecret = _clientSecret
                    }
                });

                var tokenResponse = await flow.RefreshTokenAsync(
                    userId,
                    connection.RefreshToken,
                    CancellationToken.None
                );

                // Update connection with new access token
                var update = Builders<UserEmailConnection>.Update
                    .Set(c => c.AccessToken, tokenResponse.AccessToken)
                    .Set(c => c.TokenExpiry, DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresInSeconds ?? 3600));

                await _connectionCollection.UpdateOneAsync(c => c.Id == connection.Id, update);

                return tokenResponse.AccessToken;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to refresh token for user {userId}");
                throw;
            }
        }

        // Get user's Gmail email address from access token
        private async Task<string> GetUserEmailFromToken(string accessToken)
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

            var response = await httpClient.GetAsync("https://www.googleapis.com/gmail/v1/users/me/profile");
            response.EnsureSuccessStatusCode();

            var profile = await response.Content.ReadFromJsonAsync<GmailProfile>();
            return profile?.EmailAddress ?? "unknown@gmail.com";
        }

        // Get connection status for a user
        public async Task<UserEmailConnection?> GetConnectionAsync(string userId)
        {
            return await _connectionCollection
                .Find(c => c.UserId == userId && c.IsActive)
                .FirstOrDefaultAsync();
        }

        // Disconnect Gmail for a user
        public async Task<bool> DisconnectAsync(string userId)
        {
            var update = Builders<UserEmailConnection>.Update
                .Set(c => c.IsActive, false);

            var result = await _connectionCollection.UpdateOneAsync(
                c => c.UserId == userId,
                update
            );

            return result.ModifiedCount > 0;
        }

        // Get all active connections (for background sync)
        public async Task<List<UserEmailConnection>> GetAllActiveConnectionsAsync()
        {
            return await _connectionCollection
                .Find(c => c.IsActive)
                .ToListAsync();
        }

        private class GmailProfile
        {
            public string? EmailAddress { get; set; }
        }
    }
}