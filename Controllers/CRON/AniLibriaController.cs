using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JacRed.Engine.CORE;
using Microsoft.AspNetCore.Mvc;
using JacRed.Engine;
using JacRed.Models.Details;
using JacRed.Models.AniLibV1;

namespace JacRed.Controllers.CRON
{
	[Route("/cron/anilibria/[action]")]
	public class AniLibriaController : BaseController
	{
		static bool workParse = false;

		async public Task<string> Parse(int limit)
		{
			if (workParse)
				return "work";

			workParse = true;

			if (limit == 0)
			{
				limit = 40;
			}

			try
			{
				int page = 1;
				int perPage = 40;
				int fetched = 0;

				while (fetched < limit)
				{
					string url = $"{AppInit.conf.Anilibria.rqHost()}/api/v1/anime/torrents?page={page}&limit={perPage}&include=release";
					var resp = await HttpClient.Get<TorrentsResponse>(url, IgnoreDeserializeObject: true, useproxy: AppInit.conf.Anilibria.useproxy);
					if (resp == null || resp.data == null || resp.data.Count == 0)
						break;

					var torrents = new List<TorrentBaseDetails>();
					foreach (var item in resp.data)
					{
						if (item.release == null || string.IsNullOrWhiteSpace(item.magnet))
							continue;

						string nameRu = item.release.name?.main;
						string nameEn = item.release.name?.english;
						int year = item.release.year;
						string quality = item.quality?.description;
						string codec = item.codec?.label;

						if (string.IsNullOrWhiteSpace(nameRu) || string.IsNullOrWhiteSpace(nameEn))
							continue;

						// Build unique url id similar to previous scheme, but v1 based
						string urlId = $"aniliberty:{item.release.id}:{(quality ?? "")}:{(codec ?? "")}";

						DateTime createTime = default;
						if (DateTime.TryParse(item.updated_at, out DateTime upd))
							createTime = upd;
						else if (DateTime.TryParse(item.created_at, out DateTime crt))
							createTime = crt;
						if (createTime == default)
							createTime = DateTime.UtcNow;

						string title = $"{nameRu} / {nameEn} {year} [{quality}{(string.IsNullOrWhiteSpace(codec) ? "" : ", " + codec)}]";

						string sizeName = null;
						if (item.size > 0)
						{
							long bytes = item.size;
							string[] suffix = { "B", "KB", "MB", "GB", "TB" };
							int i = 0;
							double dbl = bytes;
							while (i < suffix.Length && bytes >= 1024)
							{
								dbl = bytes / 1024.0;
								bytes /= 1024;
								i++;
							}
							sizeName = string.Format("{0:N2} {1}", dbl, suffix[i]).Replace(",", ".");
						}

						torrents.Add(new TorrentBaseDetails()
						{
							trackerName = "anilibria",
							types = new string[] { "anime" },
							url = urlId,
							title = title,
							sid = item.seeders,
							pir = item.leechers,
							createTime = createTime,
							magnet = item.magnet,
							sizeName = sizeName,
							name = tParse.ReplaceBadNames(nameRu),
							originalname = tParse.ReplaceBadNames(nameEn),
							relased = year
						});
					}

					FileDB.AddOrUpdate(torrents);

					fetched += resp.data.Count;
					page++;
					await Task.Delay(200);
				}
			}
			catch { }

			workParse = false;
			return "ok";
		}
	}
}