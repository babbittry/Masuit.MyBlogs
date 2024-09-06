﻿using Masuit.MyBlogs.Core.Extensions;
using Masuit.Tools.AspNetCore.ModelBinder;
using Masuit.Tools.Core;

namespace Masuit.MyBlogs.Core.Controllers;

[Route("values")]
public sealed class ValuesController : AdminController
{
    public IVariablesService VariablesService { get; set; }

    [HttpGet("list")]
    public async Task<ActionResult> GetAll()
    {
        return ResultData(await VariablesService.GetAllNoTracking().ToListWithNoLockAsync());
    }

    [HttpPost, DistributedLockFilter]
    public async Task<ActionResult> Save([FromBodyOrDefault] Variables model)
    {
        var b = await VariablesService.AddOrUpdateSavedAsync(v => v.Key, model) > 0;
        return ResultData(null, b, b ? "保存成功" : "保存失败");
    }

    [HttpPost("{id:int}"), DistributedLockFilter]
    public ActionResult Delete(int id)
    {
        var b = VariablesService - id;
        return ResultData(null, b, b ? "删除成功" : "保存失败");
    }
}