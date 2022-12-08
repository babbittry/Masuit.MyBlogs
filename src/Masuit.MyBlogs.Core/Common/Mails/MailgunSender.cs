﻿using CacheManager.Core;
using FreeRedis;
using Hangfire;
using Masuit.Tools.Models;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Text;

namespace Masuit.MyBlogs.Core.Common.Mails;

public sealed class MailgunSender : IMailSender
{
	private readonly HttpClient _httpClient;
	private readonly IConfiguration _configuration;
	private readonly ICacheManager<List<string>> _cacheManager;
	private readonly ICacheManager<bool> _bouncedCacheManager;
	private readonly IRedisClient _redisClient;

	public MailgunSender(HttpClient httpClient, IConfiguration configuration, ICacheManager<List<string>> cacheManager, ICacheManager<bool> bouncedCacheManager, IRedisClient redisClient)
	{
		_configuration = configuration;
		_cacheManager = cacheManager;
		_bouncedCacheManager = bouncedCacheManager;
		_redisClient = redisClient;
		_httpClient = httpClient;
		_httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"api:{_configuration["MailgunConfig:apikey"]}")));
	}

	[AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
	public async Task Send(string title, string content, string tos, string clientip)
	{
		EmailAddress email = _configuration["MailgunConfig:from"];
		using var form = new MultipartFormDataContent
		{
			{ new StringContent(email,Encoding.UTF8), "from" },
			{ new StringContent(tos,Encoding.UTF8), "to" },
			{ new StringContent(title,Encoding.UTF8), "subject" },
			{ new StringContent(content,Encoding.UTF8), "html" }
		};
		await _httpClient.PostAsync($"https://api.mailgun.net/v3/{email.Domain}/messages", form);
		_redisClient.SAdd($"Email:{DateTime.Now:yyyyMMdd}", new { title, content, tos, time = DateTime.Now, clientip });
		_redisClient.Expire($"Email:{DateTime.Now:yyyyMMdd}", 86400);
	}

	public List<string> GetBounces()
	{
		EmailAddress email = _configuration["MailgunConfig:from"];
		return _cacheManager.GetOrAdd("email-bounces", _ => _httpClient.GetStringAsync($"https://api.mailgun.net/v3/{email.Domain}/bounces").ContinueWith(t =>
		{
			return t.IsCompletedSuccessfully ? ((JArray)JObject.Parse(t.Result)["items"])?.Select(x => (string)x["address"]).ToList() : new List<string>();
		}).Result);
	}

	public bool HasBounced(string address)
	{
		EmailAddress email = _configuration["MailgunConfig:from"];
		return _bouncedCacheManager.GetOrAdd("email-bounced", _ => _httpClient.GetStringAsync($"https://api.mailgun.net/v3/{email.Domain}/bounces/{address}").ContinueWith(t => t.IsCompletedSuccessfully && JObject.Parse(t.Result).ContainsKey("error")).Result);
	}

	public Task<string> AddRecipient(string email)
	{
		EmailAddress mail = _configuration["MailgunConfig:from"];
		return _httpClient.PostAsync($"https://api.mailgun.net/v3/{mail.Domain}/bounces", new MultipartFormDataContent
		{
			{ new StringContent(email,Encoding.UTF8), "address" },
			{ new StringContent("黑名单邮箱",Encoding.UTF8), "error" }
		}).ContinueWith(t =>
		{
			var resp = t.Result;
			if (resp.IsSuccessStatusCode)
			{
				return (string)JObject.Parse(resp.Content.ReadAsStringAsync().Result)["message"];
			}
			return "添加失败";
		});
	}
}