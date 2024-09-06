﻿using AutoMapper;
using Hangfire;
using Masuit.MyBlogs.Core.Configs;
using Masuit.MyBlogs.Core.Extensions.Firewall;
using Masuit.MyBlogs.Core.Extensions.Hangfire;
using Masuit.Tools.Mime;
using Masuit.Tools.AspNetCore.ResumeFileResults.Extensions;
using Masuit.Tools.Logging;
using System.Net;
using System.Web;
using Dispose.Scope;
using FreeRedis;
using Masuit.MyBlogs.Core.Extensions;

namespace Masuit.MyBlogs.Core.Controllers;

/// <summary>
/// 登录授权
/// </summary>
[ApiExplorerSettings(IgnoreApi = true), ServiceFilter(typeof(FirewallAttribute))]
public sealed class PassportController : Controller
{
    /// <summary>
    /// 用户
    /// </summary>
    public IUserInfoService UserInfoService { get; set; }

    public IFirewallRepoter FirewallRepoter { get; set; }

    public IMapper Mapper { get; set; }

    /// <summary>
    /// 客户端的真实IP
    /// </summary>
    public string ClientIP => HttpContext.Connection.RemoteIpAddress.ToString();

    /// <summary>
    ///
    /// </summary>
    /// <param name="data"></param>
    /// <param name="isTrue"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    private ActionResult ResultData(object data, bool isTrue = true, string message = "")
    {
        return Json(new
        {
            Success = isTrue,
            Message = message,
            Data = data
        });
    }

    /// <summary>
    /// 登录页
    /// </summary>
    /// <returns></returns>
    public ActionResult Login()
    {
        var keys = RsaCrypt.GenerateRsaKeys(RsaKeyType.PKCS1);
        Response.Cookies.Append(nameof(keys.PublicKey), keys.PublicKey, new CookieOptions()
        {
            SameSite = SameSiteMode.Lax
        });
        HttpContext.Session.Set(nameof(keys.PrivateKey), keys.PrivateKey);
        string from = Request.Query["from"];
        if (!string.IsNullOrEmpty(from))
        {
            from = HttpUtility.UrlDecode(from);
            Response.Cookies.Append("refer", from, new CookieOptions()
            {
                SameSite = SameSiteMode.Lax
            });
        }

        if (HttpContext.Session.Get<UserInfoDto>(SessionKey.UserInfo) != null)
        {
            if (string.IsNullOrEmpty(from))
            {
                return RedirectToAction("Index", "Home");
            }

            return LocalRedirect(from);
        }

        if (Request.Cookies.Count > 2)
        {
            string name = Request.Cookies["username"];
            string pwd = Request.Cookies["password"]?.DesDecrypt(AppConfig.BaiduAK);
            var userInfo = UserInfoService.Login(name, pwd);
            if (userInfo != null)
            {
                Response.Cookies.Append("username", name, new CookieOptions()
                {
                    Expires = DateTime.Now.AddYears(1),
                    SameSite = SameSiteMode.Lax
                });
                Response.Cookies.Append("password", Request.Cookies["password"], new CookieOptions()
                {
                    Expires = DateTime.Now.AddYears(1),
                    SameSite = SameSiteMode.Lax
                });
                HttpContext.Session.Set(SessionKey.UserInfo, userInfo);
                BackgroundJob.Enqueue<IHangfireBackJob>(job => job.LoginRecord(userInfo, ClientIP, LoginType.Default));
                if (string.IsNullOrEmpty(from))
                {
                    return RedirectToAction("Index", "Home");
                }

                return LocalRedirect(from);
            }
        }

        return View();
    }

    /// <summary>
    /// 登陆检查
    /// </summary>
    /// <param name="username"></param>
    /// <param name="password"></param>
    /// <param name="valid"></param>
    /// <param name="remem"></param>
    /// <returns></returns>
    [HttpPost, ValidateAntiForgeryToken, DistributedLockFilter]
    public ActionResult Login([FromServices] IRedisClient cacheManager, string username, string password, string valid, string remem)
    {
        string validSession = HttpContext.GetRedisSession<string>("valid") ?? string.Empty; //将验证码从Session中取出来，用于登录验证比较
        if (string.IsNullOrEmpty(validSession) || !valid.Trim().Equals(validSession, StringComparison.InvariantCultureIgnoreCase))
        {
            return ResultData(null, false, "验证码错误");
        }

        HttpContext.RemoveRedisSession("valid"); //验证成功就销毁验证码Session，非常重要
        if (string.IsNullOrEmpty(username.Trim()) || string.IsNullOrEmpty(password.Trim()))
        {
            return ResultData(null, false, "用户名或密码不能为空");
        }

        try
        {
            var privateKey = HttpContext.Session.Get<string>(nameof(RsaKey.PrivateKey));
            password = password.RSADecrypt(privateKey);
        }
        catch (Exception)
        {
            LogManager.Info("登录失败，私钥：" + HttpContext.Session.Get<string>(nameof(RsaKey.PrivateKey)));
            throw;
        }
        var userInfo = UserInfoService.Login(username, password);
        if (userInfo == null)
        {
            var times = cacheManager.Incr("LoginError:" + ClientIP);
            if (times > 30)
            {
                FirewallRepoter.ReportAsync(IPAddress.Parse(ClientIP)).ContinueWith(_ => LogManager.Info($"多次登录用户名或密码错误，疑似爆破行为，已上报IP{ClientIP}至：" + FirewallRepoter.ReporterName));
            }

            return ResultData(null, false, "用户名或密码错误");
        }

        HttpContext.Session.Set(SessionKey.UserInfo, userInfo);
        if (remem.Trim().Contains(["on", "true"])) //是否记住登录
        {
            Response.Cookies.Append("username", HttpUtility.UrlEncode(username.Trim()), new CookieOptions()
            {
                Expires = DateTime.Now.AddYears(1),
                SameSite = SameSiteMode.Lax
            });
            Response.Cookies.Append("password", password.Trim().DesEncrypt(AppConfig.BaiduAK), new CookieOptions()
            {
                Expires = DateTime.Now.AddYears(1),
                SameSite = SameSiteMode.Lax
            });
        }

        BackgroundJob.Enqueue<IHangfireBackJob>(job => job.LoginRecord(userInfo, ClientIP, LoginType.Default));
        string refer = Request.Cookies["refer"];
        Response.Cookies.Delete(nameof(RsaKey.PublicKey), new CookieOptions()
        {
            SameSite = SameSiteMode.Lax
        });
        Response.Cookies.Delete("refer", new CookieOptions()
        {
            SameSite = SameSiteMode.Lax
        });
        HttpContext.Session.Remove(nameof(RsaKey.PrivateKey));
        return ResultData(null, true, string.IsNullOrEmpty(refer) ? "/" : refer);
    }

    /// <summary>
    /// 生成验证码
    /// </summary>
    /// <returns></returns>
    public ActionResult ValidateCode([FromServices] IRedisClient redis)
    {
        string code = redis.GetOrAdd("captcha:" + ClientIP, Tools.Strings.ValidateCode.CreateValidateCode(6), TimeSpan.FromSeconds(5));
        HttpContext.SetRedisSession("valid", code); //将验证码生成到Session中
        var stream = code.CreateValidateGraphic().RegisterDisposeScope();
        return this.ResumeFile(stream, ContentType.Jpeg, "验证码.jpg");
    }

    /// <summary>
    /// 检查验证码
    /// </summary>
    /// <param name="code"></param>
    /// <returns></returns>
    [HttpPost, DistributedLockFilter]
    public ActionResult CheckValidateCode(string code)
    {
        string validSession = HttpContext.GetRedisSession<string>("valid");
        if (string.IsNullOrEmpty(validSession) || !code.Trim().Equals(validSession, StringComparison.InvariantCultureIgnoreCase))
        {
            return ResultData(null, false, "验证码错误");
        }

        return ResultData(null, false, "验证码正确");
    }

    /// <summary>
    /// 获取用户信息
    /// </summary>
    /// <returns></returns>
    public ActionResult GetUserInfo()
    {
        var user = HttpContext.Session.Get<UserInfoDto>(SessionKey.UserInfo);
#if DEBUG
        user = Mapper.Map<UserInfoDto>(UserInfoService.GetByUsername("masuit"));
#endif

        return ResultData(user);
    }

    /// <summary>
    /// 注销登录
    /// </summary>
    /// <returns></returns>
    public ActionResult Logout()
    {
        HttpContext.Session.Remove(SessionKey.UserInfo);
        Response.Cookies.Delete("username", new CookieOptions()
        {
            SameSite = SameSiteMode.Lax
        });
        Response.Cookies.Delete("password", new CookieOptions()
        {
            SameSite = SameSiteMode.Lax
        });
        HttpContext.Session.Clear();
        return Request.Method.Equals(HttpMethods.Get) ? RedirectToAction("Index", "Home") : ResultData(null, message: "注销成功！");
    }
}