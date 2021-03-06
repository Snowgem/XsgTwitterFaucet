using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LiteDB;
using Serilog;
using Tweetinvi;
using Tweetinvi.Core.Extensions;
using Tweetinvi.Logic.Model;
using Tweetinvi.Models;
using Tweetinvi.Parameters;
using XsgTwitterBot.Configuration;
using XsgTwitterBot.Models;

namespace XsgTwitterBot.Services.Impl
{
    public class BotEngine : BotEngineBase
    {
        private readonly ILogger _logger = Log.ForContext<BotEngine>();
        private readonly AppSettings _appSettings;
        private readonly IMessageParser _messageParser;
        private readonly IWithdrawalService _withdrawalService;
        private readonly IStatService _statService;
        private readonly IAmountHelper _amountHelper;
        private readonly LiteCollection<Reward> _rewardCollection;
        private readonly LiteCollection<FriendTagMap> _friendTagMapCollection;
        private readonly LiteCollection<UserTweetMap> _userTweetMapCollection;
        private readonly LiteCollection<MessageCursor> _messageCursorCollection;
        private readonly LiteCollection<AddressToUserMap> _addressToUserMapCollection;

        public BotEngine(AppSettings appSettings,
            IMessageParser messageParser, 
            IWithdrawalService withdrawalService,
            IStatService statService,
            IAmountHelper amountHelper,
            LiteCollection<Reward> rewardCollection, 
            LiteCollection<FriendTagMap> friendTagMapCollection,
            LiteCollection<UserTweetMap> userTweetMapCollection,
            LiteCollection<MessageCursor> messageCursorCollection,
            LiteCollection<AddressToUserMap> addressToUserMapCollection)
        {
            _appSettings = appSettings;
            _messageParser = messageParser;
            _withdrawalService = withdrawalService;
            _statService = statService;
            _amountHelper = amountHelper;
            _rewardCollection = rewardCollection;
            _friendTagMapCollection = friendTagMapCollection;
            _userTweetMapCollection = userTweetMapCollection;
            _messageCursorCollection = messageCursorCollection;
            _addressToUserMapCollection = addressToUserMapCollection;
        }
        
        protected override void RunLoop()
        {
            var sleepMultiplier = 1;

            SetUserCredentials();
            
            while (!CancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    long? lastMessageId = _messageCursorCollection.FindById("current")?.Value ?? _appSettings.BotSettings.LastMessageId;
                    
                    var folowerIds = User.GetFollowerIds("GiveawayXsg", 5000).ToList();
                    var friendIds = User.GetFriendIds("GiveawayXsg", 5000).ToList();
                    var usersToFollow = folowerIds.Except(friendIds).ToList();
                    usersToFollow.ForEach(u => User.FollowUser(u));
                   
                    var messages = Message.GetLatestMessages(new GetMessagesParameters
                    {
                        Count = 50
                    })?.Where(x => x.Id > lastMessageId.GetValueOrDefault(0)).ToList();

                    if (messages == null)
                    {
                        _logger.Information("Rate limit reached.");
                        CancellationTokenSource.Token.WaitHandle.WaitOne(_appSettings.ProcessingFrequency * 5);
                        continue;
                    }

                    if (messages.Count > 0)
                    {
                        var maxMessageId = messages.Max(x => x.Id);
                        
                        _messageCursorCollection.Upsert("current", new MessageCursor
                        {
                            Id = "current",
                            Value = maxMessageId
                        });
                        
                        _logger.Information("Last message id: {lastMessageId}", maxMessageId);
                    }
                    
                    foreach (var message in messages)
                    {
                        var url = message?.Entities?.Urls.Select(u => u.ExpandedURL).FirstOrDefault();

                        if(url == null)
                            continue;
                        
                        var strTweetId = new Uri(url).Segments.LastOrDefault();
                        if (strTweetId != null)
                        {
                            if (long.TryParse(strTweetId, out var tweetId))
                            {
                                try
                                {
                                    var tweet = Tweet.GetTweet(tweetId);
                                    if(tweet == null)
                                        continue;

                                    if ((DateTime.UtcNow - tweet.CreatedBy.CreatedAt).Days < 10 || tweet.CreatedBy.DefaultProfileImage ) //|| !tweet.CreatedBy.Verified || tweet.CreatedBy.FollowersCount < 5)
                                    {
                                        _logger.Information("Ignoring tweet from user {@User} - probably a fake account.", tweet.CreatedBy);
                                        Message.PublishMessage($"Response to tweet ({tweet.Id}) - Are you using a fake account?", tweet.CreatedBy.Id);
                                        tweet.CreatedBy.BlockUser();
                                        continue;
                                    }

                                    var isProcessed = _userTweetMapCollection.FindById($"{tweet.CreatedBy.Id}@{tweet.Id}");
                                    if (isProcessed != null)
                                    {
                                        _logger.Information("Ignoring tweet from user {@User} - processed already.", tweet.CreatedBy);
                                        Message.PublishMessage($"Response to tweet ({tweet.Id}) - your tweet has been processed already.", tweet.CreatedBy.Id);
                                        continue;
                                    }
                                    
                                    _logger.Information("Received tweet ({Id}) '{Text}' from {Name} ", tweet.Id, tweet.FullText, tweet.CreatedBy.Name);
                                    
                                    // tweet can not be a reply
                                    if (!string.IsNullOrWhiteSpace(tweet.InReplyToScreenName))
                                    {
                                        _logger.Information("Ignoring tweet from user {@User} - replies are not supported.", tweet.CreatedBy);
                                        Message.PublishMessage($"Response to tweet ({tweet.Id}) - replies are not supported.", tweet.CreatedBy.Id);
                                        continue;
                                    }

                                    // tweet must contain hashtags
                                    var requiredHashTags = _appSettings.BotSettings.TrackKeywords;
                                    var tweetHashTags = tweet.Hashtags.Select(x => x.Text).ToList();
                                    var hasValidHashTags =  requiredHashTags.All(h => tweetHashTags.Contains(h));
                                    if(!hasValidHashTags)
                                    {
                                        _logger.Information("Ignoring tweet from user {@User} - tweet should contain the following hashtags", tweet.CreatedBy);
                                        Message.PublishMessage($"Response to tweet ({tweet.Id}) - Tweet should contain the following hashtags: {String.Join(" ", _appSettings.BotSettings.TrackKeywords)}", tweet.CreatedBy.Id);
                                        continue;
                                    }
                                    
                                    // tweet must contain valid xsg address
                                    var text = Regex.Replace(tweet.FullText, @"\r\n?|\n|\(|\)", " ");
                                    var targetAddress = _messageParser.GetValidAddressAsync(text).GetAwaiter().GetResult();
                                    if (string.IsNullOrWhiteSpace(targetAddress))
                                    {
                                        _logger.Information("Ignoring tweet from user {@User} - Tweet should contain valid xsg transparent address", tweet.CreatedBy);
                                        Message.PublishMessage($"Response to tweet ({tweet.Id}) - Tweet should contain valid xsg transparent address", tweet.CreatedBy.Id);
                                        continue;
                                    }

                                    var addressToUserMap = _addressToUserMapCollection.FindById(targetAddress);
                                    if (addressToUserMap == null)
                                    {
                                        _addressToUserMapCollection.Insert(new AddressToUserMap
                                        {
                                            Id = targetAddress,
                                            UserId = tweet.CreatedBy.Id
                                        });
                                    }
                                    else if (addressToUserMap.UserId != tweet.CreatedBy.Id)
                                    {
                                        _logger.Information("Ignoring tweet from user {@User} - probably a fake account - used same address", tweet.CreatedBy);
                                        Message.PublishMessage($"Response to tweet ({tweet.Id}) - Are you using a fake account?", tweet.CreatedBy.Id);
                                        tweet.CreatedBy.BlockUser();
                                    }
                                    

                                    // user can not be a scammer
                                    //var isUserLegit = ValidateUser(tweet.CreatedBy);
                                    //if (!isUserLegit)
                                    //{
//                                        _logger.Information("Ignoring tweet from user {@User}", tweet.CreatedBy);
//                                        Message.PublishMessage($"Response to tweet ({tweet.Id}) - Is your account a fake one? You you have to little followers.", tweet.CreatedBy.Id);
//                                        continue;
//                                    }
                                    
                                    // tweet can not be too short
                                    var isTweetTextValid = ValidateTweetText(tweet.Text);
                                    if (!isTweetTextValid)
                                    {
                                        _logger.Information("Ignoring tweet from user {@User} - Your tweet is too short", tweet.CreatedBy);
                                        Message.PublishMessage($"Response to tweet ({tweet.Id}) - Your tweet is too short", tweet.CreatedBy.Id);
                                        continue;
                                    }

                                    // --- processing payout
                                    var rewardType = GetRewardType(tweet);
                                    var reward = _rewardCollection.FindOne(x => x.Id == tweet.CreatedBy.Id);
                                    var replyMessage = reward != null
                                        ? HandleExistingUser(tweet, targetAddress, reward, rewardType)
                                        : HandleNewUser(tweet, targetAddress, rewardType);

                                    _userTweetMapCollection.Insert(new UserTweetMap
                                    {
                                        Id = $"{tweet.CreatedBy.Id}@{tweet.Id}"
                                    });
                                    
                                    Tweet.PublishTweet(replyMessage, new PublishTweetOptionalParameters
                                    {
                                        InReplyToTweet = tweet
                                    });
                                    
                                    Message.PublishMessage($"Response to tweet ({tweet.Id}) - {replyMessage}", tweet.CreatedBy.Id);

                                    _logger.Information("Replied with message '{ReplyMessage}'", replyMessage);
                                    _logger.Information("Faucet balance: {balance} XSG", _withdrawalService.GetBalanceAsync().GetAwaiter().GetResult());
                                }
                                catch(Exception exception)
                                {
                                    _logger.Error(exception, "Processing tweet messages failed");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex,"Failed to process tweets");
                    sleepMultiplier = 10;
                    SetUserCredentials();
                }
                finally
                {
                    CancellationTokenSource.Token.WaitHandle.WaitOne(_appSettings.ProcessingFrequency * sleepMultiplier);
                    sleepMultiplier = 1;
                }

                
            }
        }
 
        private long? GetFriendMentioned(ITweet tweet)
        {
            var friend = tweet.UserMentions.FirstOrDefault();
            if (friend != null)
            {
                var friends = User.GetFriendIds(tweet.CreatedBy, 5000).ToList();
                return friends.FirstOrDefault(id => id == friend.Id);    
            }

            return null;
        }
       
        private bool ValidateUser(IUser user)
        {
            if (user.FriendsCount >= user.FollowersCount && user.FriendsCount > 10)
            {
                return true;
            }

            var ratio = user.FollowersCount / user.FriendsCount;
            if (ratio < _appSettings.BotSettings.UserRatio)
            {
                if (user.FollowersCount > _appSettings.BotSettings.FollowersCountThreshold)
                    return false;
            }

            return true;
        }

        private bool ValidateTweetText(string text)
        {
            return text.Length >= _appSettings.BotSettings.MinTweetLenght;
        }

        private RewardType GetRewardType(ITweet tweet)
        {
            var friendId = GetFriendMentioned(tweet);
            var rewardType = friendId.HasValue ? RewardType.FriendMention : RewardType.Tag;
            
            if (friendId.HasValue)
            {
                var id = $"{tweet.CreatedBy.Id}@{friendId}";
                var isInserted = _friendTagMapCollection.Upsert(id, new FriendTagMap
                {
                    Id = id
                });

                if (!isInserted)
                {
                    return RewardType.Tag;
                }
            }

            return rewardType;
        }
        
        private string HandleNewUser(ITweet tweet, string targetAddress, RewardType rewardType)
        {
            var canWithdraw = _withdrawalService.CanExecuteAsync(rewardType).GetAwaiter().GetResult();
            if (!canWithdraw)
            {
                _logger.Warning("Not enough funds for withdrawal.");
                return string.Format(_appSettings.BotSettings.MessageFaucetDrained, tweet.CreatedBy.Name);
            }

            _withdrawalService.ExecuteAsync(rewardType, targetAddress).GetAwaiter().GetResult();
            _statService.AddStat(DateTime.UtcNow, _amountHelper.GetAmount(rewardType), true);

            var reward = new Reward
            {
                Id = tweet.CreatedBy.Id,
                Followers = tweet.CreatedBy.FollowersCount,
                LastRewardDate = DateTime.UtcNow,
                Withdrawals = 1
            };

            _rewardCollection.Insert(reward);
 
            return string.Format(_appSettings.BotSettings.MessageRewarded, tweet.CreatedBy.ScreenName,
                _amountHelper.GetAmount(rewardType));
        }

        private string HandleExistingUser(ITweet tweet, string targetAddress, Reward reward, RewardType rewardType)
        {
            var canWithdraw = _withdrawalService.CanExecuteAsync(rewardType).GetAwaiter().GetResult();
            if (!canWithdraw)
            {
                _logger.Warning("Not enough funds for withdrawal.");
                return string.Format(_appSettings.BotSettings.MessageFaucetDrained, tweet.CreatedBy.ScreenName);
            }

            reward.Followers = tweet.CreatedBy.FollowersCount;

            if (reward.Withdrawals >= reward.Followers)
            {
                return string.Format(_appSettings.BotSettings.MessageReachedLimit, tweet.CreatedBy.ScreenName);
            }
            
            if (reward.LastRewardDate.Date.Equals(DateTime.UtcNow.Date))
            {
                return  GenerateMessageDailyLimitReached(tweet.CreatedBy.ScreenName);
            }
            
            _withdrawalService.ExecuteAsync(rewardType, targetAddress).GetAwaiter().GetResult();
            _statService.AddStat(DateTime.UtcNow, _amountHelper.GetAmount(rewardType), false);

            reward.LastRewardDate = DateTime.UtcNow;
            reward.Withdrawals++;
            _rewardCollection.Update(reward);
                
            return string.Format(_appSettings.BotSettings.MessageRewarded, tweet.CreatedBy.ScreenName, _amountHelper.GetAmount(rewardType));
        }

        private string GenerateMessageDailyLimitReached(string screenName)
        {
            var diff = DateTime.UtcNow.Date.AddDays(1) -DateTime.UtcNow;
            var hours = (int) diff.TotalHours;
            var minutes = (int) diff.TotalMinutes - hours * 60;

            var tryAgainIn = "Try again in";
            if (hours == 0)
            {
                tryAgainIn += $" {minutes} minute" + (minutes > 1 ? "s" : "");
            }
            else
            {
                tryAgainIn += $" {hours} hour" + (hours > 1 ? "s" : "");
                if (minutes > 0)
                {
                    tryAgainIn += $" {minutes} minute" + (minutes > 1 ? "s" : "");
                }
            }

            return string.Format(_appSettings.BotSettings.MessageDailyLimitReached, screenName, tryAgainIn);
        }

        private ITwitterCredentials SetUserCredentials()
        {
            return Auth.SetUserCredentials(
                _appSettings.TwitterSettings.ConsumerKey,
                _appSettings.TwitterSettings.ConsumerSecret,
                _appSettings.TwitterSettings.AccessToken,
                _appSettings.TwitterSettings.AccessTokenSecret);
        }
    }
}