﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tweetinvi;
using Tweetinvi.Core.Credentials;
using TwitchBot.Configuration;

namespace TwitchBot
{
    public class TwitchBotApplication
    {
        private System.Configuration.Configuration _appConfig;
        private TwitchBotConfigurationSection _botConfig;
        private string _connStr;
        private int _intBroadcasterID;
        private IrcClient _irc;
        private Moderator _modInstance = Moderator.Instance;
        private Timeout _timeout;
        private CmdBrdCstr _cmdBrdCstr;
        private CmdMod _cmdMod;
        private CmdGen _cmdGen;
        private bool _isSongRequestAvail;
        private bool _hasTwitterInfo;
        private SpotifyControl _spotify;
        private ErrorHandler _errHndlrInstance = ErrorHandler.Instance;

        public TwitchBotApplication(System.Configuration.Configuration appConfig)
        {
            _appConfig = appConfig;
            _connStr = appConfig.ConnectionStrings.ConnectionStrings[Program._connStrType].ConnectionString;
            _botConfig = appConfig.GetSection("TwitchBotConfiguration") as TwitchBotConfigurationSection;
            _isSongRequestAvail = false;
            _hasTwitterInfo = false;
            _timeout = new Timeout();
        }

        public async Task RunAsync()
        {
            try
            {
                /* Check if developer attempted to set up the connection string for either production or test */
                if (Program._connStrType.Equals("TwitchBotConnStrTEST"))
                    Console.WriteLine("<<<< WARNING: Connecting to testing database >>>>");

                // Attempt to connect to server
                if (!IsServerConnected(_connStr))
                {
                    _connStr = null; // clear sensitive data

                    Console.WriteLine("Datebase connection failed. Please try again");
                    Console.WriteLine();
                    Console.WriteLine("-- Common technical issues: --");
                    Console.WriteLine("1: Check if firewall settings has your client IP address.");
                    Console.WriteLine("2: Double check the connection string under 'Properties' inside 'Settings'");
                    Console.WriteLine();
                    Console.WriteLine("Shutting down now...");
                    Thread.Sleep(5000);
                    Environment.Exit(1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error Message: " + ex.Message);
                Console.WriteLine();
                Console.WriteLine("Please check the connection string for the right format inside the config file");
                Console.WriteLine("Local troubleshooting needed by author of this bot");
                Console.WriteLine();
                Console.WriteLine("Shutting down now...");
                Thread.Sleep(5000);
                Environment.Exit(1);
            }

            try
            {
                // Configure error handler singleton class
                ErrorHandler.Configure(_intBroadcasterID, _connStr, _irc, _botConfig);

                // Get broadcaster ID so the user can only see their data from the db
                _intBroadcasterID = getBroadcasterID(_botConfig.Broadcaster.ToLower());

                // Add broadcaster as new user to database
                if (_intBroadcasterID == 0)
                {
                    string query = "INSERT INTO tblBroadcasters (username) VALUES (@username)";

                    using (SqlConnection conn = new SqlConnection(_connStr))
                    using (SqlCommand cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.Add("@username", SqlDbType.VarChar, 30).Value = _botConfig.Broadcaster;

                        conn.Open();
                        cmd.ExecuteNonQuery();
                        conn.Close();
                    }

                    _intBroadcasterID = getBroadcasterID(_botConfig.Broadcaster.ToLower());

                    // Try looking for the broadcaster's ID again
                    if (_intBroadcasterID == 0)
                    {
                        Console.WriteLine("Cannot find a broadcaster ID for you. "
                            + "Please contact the author with a detailed description of the issue");
                        Thread.Sleep(3000);
                        Environment.Exit(1);
                    }
                }

                /* Connect to local Spotify client */
                _spotify = new SpotifyControl(_botConfig);
                _spotify.Connect();

                Console.WriteLine();
                Console.WriteLine("Time to get to work!");
                Console.WriteLine();

                /* Make sure usernames are set to lowercase for the rest of the application */
                string _strBotName = _botConfig.BotName.ToLower();
                string _strBroadcasterName = _botConfig.Broadcaster.ToLower();

                // Password from www.twitchapps.com/tmi/
                // include the "oauth:" portion
                // Use chat bot's oauth
                /* main server: irc.twitch.tv, 6667 */
                _irc = new IrcClient("irc.twitch.tv", 6667, _strBotName, _botConfig.TwitchOAuth, _strBroadcasterName);
                _cmdGen = new CmdGen(_irc, _spotify, _botConfig, _connStr, _intBroadcasterID);
                _cmdBrdCstr = new CmdBrdCstr(_irc, _botConfig, _connStr, _intBroadcasterID, _appConfig);
                _cmdMod = new CmdMod(_irc, _timeout, _botConfig, _connStr, _intBroadcasterID, _appConfig);

                // Grab channel info
                ChannelJSON chlJSON = TaskJSON.GetChannel(_botConfig.Broadcaster, _botConfig.TwitchClientId).Result;
                int _intFollowers = chlJSON.followers;
                string _strBroadcasterGame = chlJSON.game;

                /* Whisper broadcaster bot settings */
                Console.WriteLine("---> Extra Bot Settings <---");
                Console.WriteLine("Discord link: " + _botConfig.DiscordLink);
                Console.WriteLine("Currency type: " + _botConfig.CurrencyType);
                Console.WriteLine("Enable Auto Tweets: " + _botConfig.EnableTweets);
                Console.WriteLine("Enable Auto Display Songs: " + _botConfig.EnableDisplaySong);
                Console.WriteLine("Stream latency: " + _botConfig.StreamLatency + " second(s)");
                Console.WriteLine();

                /* Start listening for delayed messages */
                DelayMsg delayMsg = new DelayMsg(_irc);
                delayMsg.Start();

                /* Pull list of mods from database */
                _modInstance.setLstMod(_connStr, _intBroadcasterID);

                /* Get list of timed out users from database */
                setListTimeouts();

                /* Ping to twitch server to prevent auto-disconnect */
                PingSender ping = new PingSender(_irc);
                ping.Start();

                /* Remind viewers of bot's existance */
                PresenceReminder preRmd = new PresenceReminder(_irc);
                preRmd.Start();

                /* Authenticate to Twitter if possible */
                if (!string.IsNullOrEmpty(_botConfig.TwitterConsumerKey) 
                    && !string.IsNullOrEmpty(_botConfig.TwitterConsumerSecret) 
                    && !string.IsNullOrEmpty(_botConfig.TwitterAccessToken) 
                    && !string.IsNullOrEmpty(_botConfig.TwitterAccessSecret))
                {
                    Auth.ApplicationCredentials = new TwitterCredentials(
                        _botConfig.TwitterConsumerKey, _botConfig.TwitterConsumerSecret,
                        _botConfig.TwitterAccessToken, _botConfig.TwitterAccessSecret
                    );

                    _hasTwitterInfo = true;
                }

                /* Finished setup, time to start */
                await GetChatBox(_isSongRequestAvail, _botConfig.TwitchAccessToken, _hasTwitterInfo);
            }
            catch (Exception ex)
            {
                _errHndlrInstance.LogError(ex, "TwitchBotApplication", "RunAsync()", true);
            }
        }

        /// <summary>
        /// Test that the server is connected
        /// </summary>
        /// <param name="connectionString">The connection string</param>
        /// <returns>true if the connection is opened</returns>
        private bool IsServerConnected(string connectionString)
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                try
                {
                    connection.Open();
                    return true;
                }
                catch (SqlException)
                {
                    return false;
                }
            }
        }

        private int getBroadcasterID(string strBroadcaster)
        {
            int intBroadcasterID = 0;

            using (SqlConnection conn = new SqlConnection(_connStr))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand("SELECT * FROM tblBroadcasters WHERE username = @username", conn))
                {
                    cmd.Parameters.AddWithValue("@username", strBroadcaster);
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.HasRows)
                        {
                            while (reader.Read())
                            {
                                if (strBroadcaster.Equals(reader["username"].ToString().ToLower()))
                                {
                                    intBroadcasterID = int.Parse(reader["id"].ToString());
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            return intBroadcasterID;
        }

        private void setListTimeouts()
        {
            try
            {
                string query = "DELETE FROM tblTimeout WHERE broadcaster = @broadcaster AND timeout < GETDATE()";

                // Create connection and command
                using (SqlConnection conn = new SqlConnection(_connStr))
                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.Add("@broadcaster", SqlDbType.Int).Value = _intBroadcasterID;

                    conn.Open();
                    cmd.ExecuteNonQuery();
                    conn.Close();
                }

                Dictionary<string, DateTime> dicTimeout = new Dictionary<string, DateTime>();

                using (SqlConnection conn = new SqlConnection(_connStr))
                {
                    conn.Open();
                    using (SqlCommand cmd = new SqlCommand("SELECT * FROM tblTimeout WHERE broadcaster = @broadcaster", conn))
                    {
                        cmd.Parameters.Add("@broadcaster", SqlDbType.Int).Value = _intBroadcasterID;
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.HasRows)
                            {
                                while (reader.Read())
                                {
                                    dicTimeout.Add(reader["username"].ToString(), Convert.ToDateTime(reader["timeout"]));
                                }
                            }
                        }
                    }
                }

                _timeout.setLstTimeout(dicTimeout);
            }
            catch (Exception ex)
            {
                _errHndlrInstance.LogError(ex, "TwitchBotApplication", "setListTimeouts()", true);
            }
        }

        /// <summary>
        /// Monitor chat box for commands
        /// </summary>
        /// <param name="isSongRequestAvail"></param>
        /// <param name="twitchAccessToken"></param>
        /// <param name="hasTwitterInfo"></param>
        private async Task GetChatBox(bool isSongRequestAvail, string twitchAccessToken, bool hasTwitterInfo)
        {
            try
            {
                /* Master loop */
                while (true)
                {
                    // Read any message inside the chat room
                    string message = _irc.readMessage();
                    Console.WriteLine(message); // Print raw irc message

                    if (!string.IsNullOrEmpty(message))
                    {
                        /* 
                        * Get user name and message from chat 
                        * and check if user has access to certain functions
                        */
                        if (message.Contains("PRIVMSG"))
                        {
                            // Modify message to only show user and message
                            int intIndexParseSign = message.IndexOf('!');
                            StringBuilder strBdrMessage = new StringBuilder(message);
                            string strUserName = message.Substring(1, intIndexParseSign - 1);

                            intIndexParseSign = message.IndexOf(" :");
                            strBdrMessage.Remove(0, intIndexParseSign + 2); // remove unnecessary info before and including the parse symbol
                            message = strBdrMessage.ToString();

                            /* 
                             * Broadcaster commands 
                             */
                            if (strUserName.Equals(_botConfig.Broadcaster.ToLower()))
                            {
                                /* Display bot settings */
                                if (message.Equals("!botsettings"))
                                    _cmdBrdCstr.CmdBotSettings();

                                /* Stop running the bot */
                                else if (message.Equals("!exitbot"))
                                    _cmdBrdCstr.CmdExitBot();

                                /* Manually connect to Spotify */
                                else if (message.Equals("!spotifyconnect"))
                                    _spotify.Connect();

                                /* Press local Spotify play button [>] */
                                else if (message.Equals("!spotifyplay"))
                                    _spotify.playBtn_Click();

                                /* Press local Spotify pause button [||] */
                                else if (message.Equals("!spotifypause"))
                                    _spotify.pauseBtn_Click();

                                /* Press local Spotify previous button [|<] */
                                else if (message.Equals("!spotifyprev"))
                                    _spotify.prevBtn_Click();

                                /* Press local Spotify next (skip) button [>|] */
                                else if (message.Equals("!spotifynext"))
                                    _spotify.skipBtn_Click();

                                /* Enables tweets to be sent out from this bot (both auto publish tweets and manual tweets) */
                                else if (message.Equals("!sendtweet on"))
                                    _cmdBrdCstr.CmdEnableTweet(hasTwitterInfo);

                                /* Disables tweets from being sent out from this bot */
                                else if (message.Equals("!sendtweet off"))
                                    _cmdBrdCstr.CmdDisableTweet(hasTwitterInfo);

                                /* Enables viewers to request songs (default off) */
                                else if (message.Equals("!srmode on"))
                                    _cmdBrdCstr.CmdEnableSRMode(ref isSongRequestAvail);

                                /* Disables viewers to request songs (default off) */
                                else if (message.Equals("!srmode off"))
                                    _cmdBrdCstr.CmdDisableSRMode(ref isSongRequestAvail);

                                /* Updates the title of the Twitch channel */
                                // Usage: !updatetitle [title]
                                else if (message.StartsWith("!updatetitle "))
                                    _cmdBrdCstr.CmdUpdateTitle(message, twitchAccessToken);

                                /* Updates the game of the Twitch channel */
                                // Usage: !updategame "[game]" (with quotation marks)
                                else if (message.StartsWith("!updategame "))
                                    _cmdBrdCstr.CmdUpdateGame(message, twitchAccessToken, hasTwitterInfo);

                                /* Sends a manual tweet (if credentials have been provided) */
                                // Usage: !tweet "[message]" (use quotation marks)
                                else if (message.StartsWith("!tweet "))
                                    _cmdBrdCstr.CmdTweet(hasTwitterInfo, message);

                                /* Enables songs from local Spotify to be displayed inside the chat */
                                else if (message.Equals("!displaysongs on"))
                                    _cmdBrdCstr.CmdEnableDisplaySongs();

                                /* Disables songs from local Spotify to be displayed inside the chat */
                                else if (message.Equals("!displaysongs off"))
                                    _cmdBrdCstr.CmdDisableDisplaySongs();

                                /* Add viewer to moderator list so they can have access to bot moderator commands */
                                // Usage: !addmod @[username]
                                else if (message.StartsWith("!addmod ") && message.Contains("@"))
                                    _cmdBrdCstr.CmdAddBotMod(message);

                                /* Remove moderator from list so they can't access the bot moderator commands */
                                // Usage: !delmod @[username]
                                else if (message.StartsWith("!delmod ") && message.Contains("@"))
                                    _cmdBrdCstr.CmdDelBotMod(message);

                                /* List bot moderators */
                                else if (message.Equals("!listmod"))
                                    _cmdBrdCstr.CmdListMod();

                                /* Add countdown */
                                // Usage: !addcountdown [MM-DD-YY] [hh:mm:ss] [AM/PM] [message]
                                else if (message.StartsWith("!addcountdown "))
                                    _cmdBrdCstr.CmdAddCountdown(message, strUserName);

                                /* Edit countdown details (for either date and time or message) */
                                // Usage (message): !editcountdownMSG [countdown id] [message]
                                // Usage (date and time): !editcountdownDTE [countdown id] [MM-DD-YY] [hh:mm:ss] [AM/PM]
                                else if (message.StartsWith("!editcountdown"))
                                    _cmdBrdCstr.CmdEditCountdown(message, strUserName);

                                /* List all of the countdowns the broadcaster has set */
                                else if (message.Equals("!listcountdown"))
                                    _cmdBrdCstr.CmdListCountdown(strUserName);

                                /* insert more broadcaster commands here */
                            }

                            /*
                             * Moderator commands (also checks if user has been timed out from using a command)
                             */
                            if (strUserName.Equals(_botConfig.Broadcaster) || _modInstance.LstMod.Contains(strUserName.ToLower()))
                            {
                                /* Displays Discord link into chat (if available) */
                                if (message.Equals("!discord") && !isUserTimedout(strUserName))
                                    _cmdMod.CmdDiscord();

                                /* Takes money away from a user */
                                // Usage: !charge [-amount] @[username]
                                else if (message.StartsWith("!charge ") && message.Contains("@") && !isUserTimedout(strUserName))
                                    _cmdMod.CmdCharge(message, strUserName);

                                /* Gives money to user */
                                // Usage: !deposit [amount] @[username]
                                else if (message.StartsWith("!deposit ") && message.Contains("@") && !isUserTimedout(strUserName))
                                    _cmdMod.CmdDeposit(message, strUserName);

                                /* Removes the first song in the queue of song requests */
                                else if (message.Equals("!popsr") && !isUserTimedout(strUserName))
                                    _cmdMod.CmdPopSongRequest();

                                /* Removes first party memeber in queue of party up requests */
                                else if (message.Equals("!poppartyuprequest") && !isUserTimedout(strUserName))
                                    _cmdMod.CmdPopPartyUpRequest();

                                /* Bot-specific timeout on a user for a set amount of time */
                                // Usage: !addtimeout [seconds] @[username]
                                else if (message.StartsWith("!addtimeout ") && message.Contains("@") && !isUserTimedout(strUserName))
                                    _cmdMod.CmdAddTimeout(message, strUserName);

                                /* Remove bot-specific timeout on a user for a set amount of time */
                                // Usage: !deltimeout @[username]
                                else if (message.StartsWith("!deltimeout @") && !isUserTimedout(strUserName))
                                    _cmdMod.CmdDelTimeout(message, strUserName);

                                /* Set delay for messages based on the latency of the stream */
                                // Usage: !setlatency [seconds]
                                else if (message.StartsWith("!setlatency ") && !isUserTimedout(strUserName))
                                    _cmdMod.CmdSetLatency(message, strUserName);

                                /* Add a broadcaster quote */
                                // Usage: !addquote [quote]
                                else if (message.StartsWith("!addquote ") && !isUserTimedout(strUserName))
                                    _cmdMod.CmdAddQuote(message, strUserName);

                                /* insert moderator commands here */
                            }

                            /* 
                             * General commands 
                             */
                            /* Display some viewer commands a link to command documentation */
                            if (message.Equals("!cmds") && !isUserTimedout(strUserName))
                                _cmdGen.CmdCmds();

                            /* Display a static greeting */
                            else if (message.Equals("!hello") && !isUserTimedout(strUserName))
                                _cmdGen.CmdHello(strUserName);

                            /* Display the current time in UTC (Coordinated Universal Time) */
                            else if (message.Equals("!utctime") && !isUserTimedout(strUserName))
                                _cmdGen.CmdUtcTime();

                            /* Display the current time in the time zone the host is located */
                            else if (message.Equals("!hosttime") && !isUserTimedout(strUserName))
                                _cmdGen.CmdHostTime(_botConfig.Broadcaster);

                            /* Shows how long the broadcaster has been streaming */
                            else if (message.Equals("!duration") && !isUserTimedout(strUserName))
                                _cmdGen.CmdDuration();

                            /* Display list of requested songs */
                            else if (message.Equals("!srlist") && !isUserTimedout(strUserName))
                                _cmdGen.CmdListSR();

                            /* Request a song for the host to play */
                            // Usage: !sr [artist] - [song title]
                            else if (message.StartsWith("!sr ") && !isUserTimedout(strUserName))
                                _cmdGen.CmdSR(isSongRequestAvail, message, strUserName);

                            /* Displays the current song being played from Spotify */
                            else if (message.Equals("!spotifycurr") && !isUserTimedout(strUserName))
                                _cmdGen.CmdSpotifyCurr();

                            /* Slaps a user and rates its effectiveness */
                            // Usage: !slap @[username]
                            else if (message.StartsWith("!slap @") && !isUserTimedout(strUserName))
                                _cmdGen.CmdSlap(message, strUserName);

                            /* Stabs a user and rates its effectiveness */
                            // Usage: !stab @[username]
                            else if (message.StartsWith("!stab @") && !isUserTimedout(strUserName))
                                _cmdGen.CmdStab(message, strUserName);

                            /* Shoots a viewer's random body part */
                            // Usage !shoot @[username]
                            else if (message.StartsWith("!shoot @") && !isUserTimedout(strUserName))
                                _cmdGen.CmdShoot(message, strUserName);

                            /* Throws an item at a viewer and rates its effectiveness against the victim */
                            // Usage: !throw [item] @username
                            else if (message.StartsWith("!throw ") && message.Contains("@") && !isUserTimedout(strUserName))
                                _cmdGen.CmdThrow(message, strUserName);

                            /* Request party member if game and character exists in party up system */
                            // Usage: !partyup [party member name]
                            else if (message.StartsWith("!partyup ") && !isUserTimedout(strUserName))
                                _cmdGen.CmdPartyUp(message, strUserName);

                            /* Check what other user's have requested */
                            else if (message.Equals("!partyuprequestlist") && !isUserTimedout(strUserName))
                                _cmdGen.CmdPartyUpRequestList();

                            /* Check what party members are available (if game is part of the party up system) */
                            else if (message.Equals("!partyuplist") && !isUserTimedout(strUserName))
                                _cmdGen.CmdPartyUpList();

                            /* Check user's account balance */
                            else if (message.Equals("!myfunds") && !isUserTimedout(strUserName))
                                _cmdGen.CmdCheckFunds(strUserName);

                            /* Gamble money away */
                            // Usage: !gamble [money]
                            else if (message.StartsWith("!gamble ") && !isUserTimedout(strUserName))
                                _cmdGen.CmdGamble(message, strUserName);

                            /* Display random broadcaster quote */
                            else if (message.Equals("!quote") && !isUserTimedout(strUserName))
                                _cmdGen.CmdQuote();

                            /* add more general commands here */
                        }
                    }
                } // end master while loop
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                _errHndlrInstance.LogError(ex, "TwitchBotApplication", "GetChatBox(bool, string, bool)", true);
            }
        }

        private bool isUserTimedout(string strUserName)
        {
            if (_timeout.getLstTimeout().ContainsKey(strUserName))
            {
                string timeout = _timeout.getTimoutFromUser(strUserName, _intBroadcasterID, _connStr);

                if (timeout.Equals("0 seconds"))
                    _irc.sendPublicChatMessage("You are now allowed to talk to me again @" + strUserName
                        + ". Please try the requested command once more");
                else
                    _irc.sendPublicChatMessage("I am not allowed to talk to you for " + timeout);

                return true;
            }

            return false;
        }

    }
}