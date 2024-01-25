using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Commands.Shared;
using Coflnet.Sky.Core;
using Coflnet.Sky.McConnect.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Coflnet.Sky.Commands.MC;

/// <summary>
/// Handles finding people who circumvent delay
/// </summary>
public class CircumventTracker
{
    private IConnectApi connectApi;
    private ILogger<CircumventTracker> logger;

    private ConcurrentDictionary<string, FlipInstance> lastSeen = new();

    public CircumventTracker(IConnectApi connectApi, ILogger<CircumventTracker> logger)
    {
        this.connectApi = connectApi;
        this.logger = logger;
    }

    public void Callenge(IMinecraftSocket socket)
    {
        socket.TryAsyncTimes(async () =>
        {
            if(socket.SessionInfo.NotPurchaseRate == 0)
                return;
            using var challenge = socket.CreateActivity("challengeCreate", socket.ConSpan);
            var auction = await FindAuction(socket) ?? throw new CoflnetException("no_auction", "No auction found");
            var lowPriced = new LowPricedAuction()
            {
                Auction = auction,
                TargetPrice = auction.StartingBid + (long)(socket.Settings.MinProfit * (1 + Random.Shared.NextDouble())),
                AdditionalProps = new(),
                DailyVolume = (int)socket.Settings.MinVolume + 1,
                Finder = socket.Settings.AllowedFinders.HasFlag(LowPricedAuction.FinderType.SNIPER) ? LowPricedAuction.FinderType.SNIPER : LowPricedAuction.FinderType.SNIPER_MEDIAN
            };
            var flip = FlipperService.LowPriceToFlip(lowPriced);
            var isMatch = socket.Settings.MatchesSettings(flip);
            if (isMatch.Item1)
            {
                lastSeen.TryAdd(socket.UserId, flip);
                return;
            }
            logger.LogError("Testflip doesn't match {UserId} ({socket.SessionInfo.McUuid}) {flip}", socket.UserId, socket.SessionInfo.McUuid, JsonConvert.SerializeObject(lowPriced));
        }, "creating challenge");
    }

    public void Shedule(IMinecraftSocket socket)
    {
        if (!lastSeen.TryRemove(socket.UserId, out var flip))
            return;
        socket.TryAsyncTimes(async () =>
        {
            using var challenge = socket.CreateActivity("challenge", socket.ConSpan);
            challenge.Log($"Choosen auction id {flip.Auction.Uuid}");
            await Task.Delay(TimeSpan.FromSeconds(2 + Random.Shared.NextDouble() * 3));
            await connectApi.ConnectChallengePostAsync(new()
            {
                AuctionUuid = flip.Auction.Uuid,
                MinecraftUuid = socket.SessionInfo.McUuid,
                UserId = socket.UserId
            });
            await socket.SendFlip(flip);
        }, "sheduling challenge");
    }

    private static async Task<SaveAuction> FindAuction(IMinecraftSocket socket)
    {
        foreach (var blocked in socket.TopBlocked.Where(b => b.Flip.Auction.Start < DateTime.UtcNow - TimeSpan.FromMinutes(1)))
        {
            if (blocked.Reason != "minProfit" && blocked.Reason != "minVolume")
                continue;
            return blocked.Flip.Auction;
        }
        using var context = new HypixelContext();
        return await context.Auctions.OrderByDescending(a => a.Id)
            .Take(250)
            .Where(a => a.HighestBidAmount == 0).FirstOrDefaultAsync();
    }

    public class State
    {
        public string AuctionId;
    }

}