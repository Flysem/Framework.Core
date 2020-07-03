using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autofac;
using Autofac.Extras.DynamicProxy;
using Framework.Core.CodeTemplate;
using Framework.Core.Common;
using Framework.Core.Common.Hubs;
using Framework.Core.Extensions;
using Framework.Core.Extensions.Quartz;
using Framework.Core.Filter;
using Framework.Core.Middlewares;
using Framework.Core.Middlewares.webSocket;
using Framework.Core.Models.ViewModels;
using Grpc.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using SqlSugar;
using Swashbuckle.AspNetCore.Filters;
using static Framework.Core.QuartzServices;

namespace Framework.Core
{
    public class Startup
    {

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration; 
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            //services.AddGrpc();
            services.AddSingleton(new Appsettings());
            services.AddScoped<ICache, MemoryCaching>();
            services.AddScoped<IUser, AspNetUser>();
            services.AddSingleton<TemplateConfig>();
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddSingleton<IMemoryCache>(factory =>
            {
                var cache = new MemoryCache(new MemoryCacheOptions());
                return cache;
            });
            services.AddScoped<SqlSugar.ISqlSugarClient>(p => DBClientManage.GetSqlSugarClient());
            services.AddSingleton<QuartzServicesClient>(p =>
            {
                Channel _channel = new Channel(Appsettings.app(new string[] { "AppSettings", "gRPCClient", "ConnectionString" }), ChannelCredentials.Insecure);
                return new QuartzServicesClient(_channel);
            });
            services.AddSignalR();
            services.AddScoped<ICodeContext, CodeContext>();
            services.AddAutoMapperSetup();
            services.AddWebSocketManager();
            services.AddQuartzManager();
            var jwtSetting = ServerJwtSetting.GetJwtSetting();

            // ��ɫ��ӿڵ�Ȩ��Ҫ�����
            var permissionRequirement = new PermissionRequirement(
                "/api/error",// �ܾ���Ȩ����ת��ַ��Ŀǰ���ã�
                new List<PermissionItemView>(),
                ClaimTypes.Role,//���ڽ�ɫ����Ȩ
                jwtSetting.Issuer,//������
                jwtSetting.Audience,//����
                jwtSetting.Credentials,//ǩ��ƾ��
                expiration: TimeSpan.FromSeconds(60 * 60)//�ӿڵĹ���ʱ��
                );

            services.AddAuthorization(options =>
            {
                options.AddPolicy("public", policy => policy.RequireRole("public").Build());
            });
            // 3�����ӵĲ�����Ȩ
            services.AddAuthorization(options =>
            {
                options.AddPolicy(Permissions.Name,
                         policy => policy.Requirements.Add(permissionRequirement));
            });

            // ע��Ȩ�޴�����
            services.AddScoped<IAuthorizationHandler, PermissionHandler>();
            services.AddSingleton(permissionRequirement);

            services.AddAuthentication(o =>
            {
                o.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = nameof(ApiResponseHandler);
                o.DefaultForbidScheme = nameof(ApiResponseHandler);
            })
               .AddJwtBearer(options =>
               {
                   options.Events = new JwtBearerEvents()
                   {
                       OnAuthenticationFailed = context =>
                       {
                           // ������ڣ����<�Ƿ����>��ӵ�������ͷ��Ϣ��
                           if (context.Exception.GetType() == typeof(SecurityTokenExpiredException))
                           {
                               context.Response.Headers.Add("Token-Expired", "true");
                           }
                           return Task.CompletedTask;
                       }
                   };

                   options.TokenValidationParameters = new TokenValidationParameters
                   {
                       ValidIssuer = jwtSetting.Issuer,
                       ValidAudience = jwtSetting.Audience,
                       IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSetting.SecurityKey)),
                       ClockSkew = TimeSpan.Zero
                   };
               })
               .AddScheme<AuthenticationSchemeOptions, ApiResponseHandler>(nameof(ApiResponseHandler), o => { });

            services.AddSwaggerGen(option =>
            {
                option.SwaggerDoc("BlogVue", new OpenApiInfo
                {
                    Version = "v1",
                    Title = "Framework.Core API",
                    Description = "API for Framework.Core",
                });
                // ������ȨС��
                option.OperationFilter<AddResponseHeadersFilter>();
                option.OperationFilter<AppendAuthorizeToSummaryOperationFilter>();

                // ��header�����token�����ݵ���̨
                option.OperationFilter<SecurityRequirementsOperationFilter>();

                // ������ oauth2
                option.AddSecurityDefinition("oauth2", new OpenApiSecurityScheme
                {
                    Description = "JWT��Ȩ���¿������� Bearer Tokenֵ��ע������֮����һ���ո�",
                    Name = "Authorization",//jwtĬ�ϵĲ�������
                    In = ParameterLocation.Header,//jwtĬ�ϴ��Authorization��Ϣ��λ��(����ͷ��)
                    Type = SecuritySchemeType.ApiKey
                });
                //// ����apixml����
                option.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{typeof(Startup).Assembly.GetName().Name}.xml"), true);
            });

            services.AddControllers(o =>
            {
                // ȫ���쳣����
                //o.Filters.Add(typeof(GlobalExceptionsFilter));
            });
            //ȥ��Json���л�DateTime���� T�ַ�
            services.AddControllers().AddJsonOptions(configure =>
            {
                configure.JsonSerializerOptions.Converters.Add(new DatetimeJsonConverter());
            });
            //DBStartup.SeedAsync().Wait();
        }


        public void ConfigureContainer(ContainerBuilder builder)
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;

            #region AOP

            var cacheType = new List<Type>();
            builder.RegisterType<FrameworkCacheAOP>();
            cacheType.Add(typeof(FrameworkCacheAOP));
            builder.RegisterType<FrameworkLogAOP>();
            cacheType.Add(typeof(FrameworkLogAOP));
            builder.RegisterType<FrameworkTranAOP>();
            cacheType.Add(typeof(FrameworkTranAOP));
            #endregion


            #region ע��Repository

            var repositoryDllFile = Path.Combine(basePath, "Framework.Core.Repository.dll");
            var assemblysRepository = Assembly.LoadFrom(repositoryDllFile);
            builder.RegisterAssemblyTypes(assemblysRepository).AsImplementedInterfaces();

            #endregion


            #region ע��Services

            var ServicesDllFile = Path.Combine(basePath, "Framework.Core.Services.dll");
            var assemblysServices = Assembly.LoadFrom(ServicesDllFile);
            builder.RegisterAssemblyTypes(assemblysServices)
                .AsImplementedInterfaces()
                 .InstancePerDependency()
                .EnableInterfaceInterceptors()//����Autofac.Extras.DynamicProxy;
                .InterceptedBy(cacheType.ToArray());//����������������б�����ע�ᡣ
            #endregion
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, WebSocketHandlerCore webSocketHandlerCode)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            //app.UseHttpsRedirection();

            app.UseCors(builder => builder.WithOrigins(Appsettings.app(new string[] { "Startup", "Cors", "IPs" }).Split(','))
            .AllowAnyHeader()
            .AllowAnyMethod()); //����

            app.UseMiddlewareAll();
            app.UseSwagger();
            app.UseSwaggerUI(option =>
            {
                option.SwaggerEndpoint("/swagger/BlogVue/swagger.json", "Framework.Core");

                option.RoutePrefix = string.Empty;
                option.DocumentTitle = "Framework.Core API";
            });

            //������̬�ļ�����
            app.UseStaticFiles();
            var filePath = Path.Combine(env.ContentRootPath, "images");
            if (!Directory.Exists(filePath)) Directory.CreateDirectory(filePath);
            app.UseStaticFiles(new StaticFileOptions
            {
                RequestPath = "/images",
                FileProvider = new PhysicalFileProvider(Path.Combine(env.ContentRootPath, "images"))
            });

            app.UseRouting();
            // �ȿ�����֤
            app.UseAuthentication();
            // Ȼ������Ȩ�м��
            app.UseAuthorization();
            app.QuartzManager();
            var webSocketOptions = new WebSocketOptions()
            {
                KeepAliveInterval = TimeSpan.FromSeconds(20),
                ReceiveBufferSize = 4 * 1024
            };
            app.UseWebSockets(webSocketOptions);
            app.MapWebSocketManager("/ws", webSocketHandlerCode);
            app.UseEndpoints(endpoints =>
            {
                //endpoints.MapGrpcService<MsgServiceImpl>();
                endpoints.MapControllers();
                endpoints.MapHub<ChatHub>("/api2/chatHub");
            });
        }
    }
}
