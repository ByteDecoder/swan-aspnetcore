﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Swan.AspNetCore.Logger;
using Swan.AspNetCore.Models;
using Swan.Formatters;

namespace Swan.AspNetCore
{
    /// <summary>
    /// Extensions methods to implement SWAN providers.
    /// </summary>
    public static class Extensions
    {
        /// <summary>
        /// The JSON MIME type.
        /// </summary>
        public const string JsonMimeType = "application/json";

        /// <summary>
        /// Uses the json exception handler.
        /// </summary>
        /// <param name="app">The application.</param>
        /// <returns>The exception handler.</returns>
        public static IApplicationBuilder UseJsonExceptionHandler(this IApplicationBuilder app) => app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                context.Response.StatusCode = 500; // or another Status accordingly to Exception Type
                context.Response.ContentType = JsonMimeType;
                var error = context.Features.Get<IExceptionHandlerFeature>();
                await context.Response.WriteAsync(Json.Serialize(error?.Error ?? new Exception("Unhandled Exception")));
            });
        });

        /// <summary>
        /// Adds the entity framework logger.
        /// </summary>
        /// <typeparam name="TDbContext">The type of the database context.</typeparam>
        /// <typeparam name="TLog">The type of the log.</typeparam>
        /// <param name="factory">The factory.</param>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The logger factory.</returns>
        /// <exception cref="ArgumentNullException">factory.</exception>
        public static ILoggerFactory AddEntityFramework<TDbContext, TLog>(
            this ILoggerFactory factory,
            IServiceProvider serviceProvider,
            Func<string, Microsoft.Extensions.Logging.LogLevel, bool>? filter = null)
            where TDbContext : DbContext
            where TLog : LogEntry, new()
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            factory.AddProvider(new EntityFrameworkLoggerProvider<TDbContext, TLog>(serviceProvider, filter));

            return factory;
        }

        /// <summary>
        /// Adds the entity framework.
        /// </summary>
        /// <typeparam name="TDbContext">The type of the database context.</typeparam>
        /// <typeparam name="TLog">The type of the log.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>
        /// The logger builder.
        /// </returns>
        /// <exception cref="ArgumentNullException">builder.</exception>
        public static ILoggingBuilder AddEntityFramework<TDbContext, TLog>(
            this ILoggingBuilder builder,
            Func<string, Microsoft.Extensions.Logging.LogLevel, bool>? filter = null)
            where TDbContext : DbContext
            where TLog : LogEntry, new()
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));

            builder.Services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            builder.AddProvider(
                new EntityFrameworkLoggerProvider<TDbContext, TLog>(builder.Services.BuildServiceProvider(), filter));

            return builder;
        }

        /// <summary>
        /// Adds the logging entity framework.
        /// </summary>
        /// <typeparam name="TDbContext">The type of the database context.</typeparam>
        /// <typeparam name="TLog">The type of the log.</typeparam>
        /// <param name="services">The services.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The services collection.</returns>
        public static IServiceCollection AddLoggingEntityFramework<TDbContext, TLog>(this IServiceCollection services,
            Func<string, Microsoft.Extensions.Logging.LogLevel, bool>? filter = null)
            where TDbContext : DbContext
            where TLog : LogEntry, new()
        {
            return services.AddLogging(logging => logging.AddEntityFramework<TDbContext, TLog>(filter));
        }

        /// <summary>
        /// Uses the bearer token provider.
        /// </summary>
        /// <param name="app">The application.</param>
        /// <param name="validationParameter">The validation parameter.</param>
        /// <param name="identityResolver">The identity resolver.</param>
        /// <param name="bearerTokenResolver">The bearer token resolver.</param>
        /// <param name="expiration">The expiration.</param>
        /// <param name="forceHttps">if set to <c>true</c> [force HTTPS].</param>
        /// <returns>The application build with bearer token authentication.</returns>
        public static IApplicationBuilder UseBearerTokenAuthentication(this IApplicationBuilder app,
            TokenValidationParameters validationParameter,
            Func<IServiceProvider, string, string, string, string, Task<ClaimsIdentity?>> identityResolver,
            Func<ClaimsIdentity, Dictionary<string, object>, Task<Dictionary<string, object>>>? bearerTokenResolver = null,
            TimeSpan expiration = default,
            bool forceHttps = true)
        {
            if (bearerTokenResolver == null)
                bearerTokenResolver = (identity, input) => Task.FromResult(input);

            // Use Authentication capabilities
            app.UseAuthentication();

            // Middleware for authenticate bearer token
            app.UseMiddleware<TokenProviderMiddleware>(Options.Create(new TokenProviderOptions
            {
                Audience = validationParameter.ValidAudience,
                Issuer = validationParameter.ValidIssuer,
                SigningCredentials =
                    new SigningCredentials(validationParameter.IssuerSigningKey, SecurityAlgorithms.HmacSha256),
                IdentityResolver = identityResolver,
                BearerTokenResolver = bearerTokenResolver,
                Expiration = expiration == default ? TimeSpan.FromMinutes(20) : expiration,
                ForceHttps = forceHttps,
            }));

            // Middleware to use authorization
            app.UseMiddleware<AuthenticateSchemeMiddleware>(JwtBearerDefaults.AuthenticationScheme);

            return app;
        }

        /// <summary>
        /// Uses the fallback to redirect everything without extension.
        /// </summary>
        /// <param name="app">The application.</param>
        /// <param name="fallbackPath">The fallback path.</param>
        /// <param name="ignoreCheck">The ignore check.</param>
        /// <returns>The application builder with the fallback.</returns>
        public static IApplicationBuilder UseFallback(this IApplicationBuilder app, string fallbackPath = "/index.html", Func<PathString, bool>? ignoreCheck = null)
        {
            if (ignoreCheck == null)
                ignoreCheck = (path) => path.StartsWithSegments("/api") == false;

            return app.Use(async (context, next) =>
            {
                await next();

                // If there's no available file and the request doesn't contain an extension, we're probably trying to access a page.
                // Rewrite request to use app root
                if (context.Response.StatusCode == (int)HttpStatusCode.NotFound
                    && ignoreCheck(context.Request.Path)
                    && !Path.HasExtension(context.Request.Path.Value))
                {
                    context.Request.Path = fallbackPath;
                    await next();
                }
            });
        }

        /// <summary>
        /// Extension method to add AuditTrail to a DbContext.
        /// </summary>
        /// <typeparam name="T">The context.</typeparam>
        /// <typeparam name="TEntity">The type of the entity.</typeparam>
        /// <param name="context">The Db context.</param>
        /// <param name="currentUserId">The Id of the principal claim.</param>
        /// <returns>The Db context with the audit controller.</returns>
        public static IBusinessDbContext UseAuditTrail<T, TEntity>(this IBusinessDbContext context, string currentUserId)
            where T : DbContext
        {
            context.AddController(new AuditTrailController<T, TEntity>((T)context, currentUserId));

            return context;
        }

        /// <summary>
        /// Adds the bearer token authentication.
        /// </summary>
        /// <param name="services">The services.</param>
        /// <param name="validationParameters">The validation parameters.</param>
        /// <returns>The service with bearer token authentication.</returns>
        public static IServiceCollection AddBearerTokenAuthentication(this IServiceCollection services,
            TokenValidationParameters validationParameters)
        {
            services
                .AddAuthentication(options =>
                {
                    // Add Authentication services
                    options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                .AddJwtBearer(options =>
                {
                    // Configure the app to use Jwt Bearer Authentication
                    options.TokenValidationParameters = validationParameters;
                });

            return services;
        }

        /// <summary>
        /// Serialize the HTTP content to a JSON as an asynchronous operation.
        /// </summary>
        /// <typeparam name="T">The type of object to return.</typeparam>
        /// <param name="httpContent">Content of the HTTP.</param>
        /// <returns>The object from the JSON.</returns>
        public static async Task<T> ReadAsJsonAsync<T>(this HttpContent httpContent)
        {
            var responseString = await httpContent.ReadAsStringAsync().ConfigureAwait(false);

            return Json.Deserialize<T>(responseString);
        }

        /// <summary>
        /// Send a GET request to the specified Uri and return the response body as a JSON in an asynchronous operation.
        /// </summary>
        /// <typeparam name="T">The type of object to return.</typeparam>
        /// <param name="client">The client.</param>
        /// <param name="requestUri">The request URI.</param>
        /// <returns>The object from the JSON.</returns>
        public static async Task<T> GetJsonAsync<T>(this HttpClient client, string requestUri)
        {
            var responseString = await client.GetStringAsync(requestUri).ConfigureAwait(false);

            return Json.Deserialize<T>(responseString);
        }

        /// <summary>
        /// Send a GET request to the specified Uri and return the response body as a JSON in an asynchronous operation.
        /// </summary>
        /// <typeparam name="T">The type of object to return.</typeparam>
        /// <param name="client">The client.</param>
        /// <param name="requestUri">The request URI.</param>
        /// <returns>The object from the JSON.</returns>
        public static async Task<T> GetJsonAsync<T>(this HttpClient client, Uri requestUri)
        {
            var responseString = await client.GetStringAsync(requestUri).ConfigureAwait(false);

            return Json.Deserialize<T>(responseString);
        }
    }
}