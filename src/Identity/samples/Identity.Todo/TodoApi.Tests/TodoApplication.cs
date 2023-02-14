// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace TodoApi.Tests;

internal class TodoApplication : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _sqliteConnection = new("Filename=:memory:");
    private readonly Action<IdentityOptions>? _configureIdentity;

    public TodoApplication(Action<IdentityOptions>? configureIdentity = null)
    {
        _configureIdentity = configureIdentity;
    }

    public TodoDbContext CreateTodoDbContext()
    {
        var db = Services.GetRequiredService<IDbContextFactory<TodoDbContext>>().CreateDbContext();
        db.Database.EnsureCreated();
        return db;
    }

    public void ConfigureIdentity(Action<IdentityOptions> configure)
    {
        var options = Services.GetRequiredService<IOptions<IdentityOptions>>().Value;
        configure(options);
    }

    public void RequireConfirmedUserEmails()
        => ConfigureIdentity(options =>
        {
            options.SignIn.RequireConfirmedAccount = true;
            options.SignIn.RequireConfirmedEmail = true;
        });

    public async Task<(string, string?)> CreateUserAsync(string username, string? password = null, bool isAdmin = false, bool generateCode = false)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TodoUser>>();
        var newUser = new TodoUser { UserName = username };
        var result = await userManager.CreateAsync(newUser, password ?? Guid.NewGuid().ToString());
        if (isAdmin)
        {
            await userManager.AddClaimAsync(newUser, new Claim(ClaimTypes.Role, "admin"));
        }
        Assert.True(result.Succeeded);
        return generateCode
            ? (newUser.Id, WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await userManager.GenerateEmailConfirmationTokenAsync(newUser))))
            : (newUser.Id, null);
    }

    public async Task DeleteUserAsync(string username)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TodoUser>>();
        var user = await userManager.FindByNameAsync(username);
        var result = await userManager.DeleteAsync(user!);
        Assert.True(result.Succeeded);
    }

    public async Task<HttpClient> CreateClientAsync(string userName)
    {
        var token = await CreateTokenAsync(userName);
        return CreateDefaultClient(new AuthHandler(req =>
        {
            req.Headers.Authorization = new AuthenticationHeaderValue(JwtBearerDefaults.AuthenticationScheme, token);
        }));
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Open the connection, this creates the SQLite in-memory database, which will persist until the connection is closed
        _sqliteConnection.Open();

        builder.ConfigureServices(services =>
        {
            // We're going to use the factory from our tests
            services.AddDbContextFactory<TodoDbContext>();

            // Throw away dataprotection
            services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();

            // We need to replace the configuration for the DbContext to use a different configured database
            services.AddDbContextOptions<TodoDbContext>(o => o.UseSqlite(_sqliteConnection));

            // Lower the requirements for the tests
            services.Configure<IdentityOptions>(o =>
            {
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireDigit = false;
                o.Password.RequiredUniqueChars = 0;
                o.Password.RequiredLength = 1;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;

                if (_configureIdentity != null)
                {
                    _configureIdentity(o);
                }
            });
        });

        return base.CreateHost(builder);
    }

    private async Task<string> CreateTokenAsync(string userName)
    {
        // Read the user JWTs configuration for testing so unit tests can generate
        // JWT tokens.
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TodoUser>>();
        var tokenService = scope.ServiceProvider.GetRequiredService<IUserTokenService<TodoUser>>();
        var user = await userManager.FindByNameAsync(userName);
        return await tokenService.GetAccessTokenAsync(user!);
    }

    protected override void Dispose(bool disposing)
    {
        _sqliteConnection?.Dispose();
        base.Dispose(disposing);
    }

    private sealed class AuthHandler : DelegatingHandler
    {
        private readonly Action<HttpRequestMessage> _onRequest;

        public AuthHandler(Action<HttpRequestMessage> onRequest)
        {
            _onRequest = onRequest;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _onRequest(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
