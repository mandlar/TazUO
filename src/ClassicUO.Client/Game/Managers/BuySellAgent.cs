﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Network;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Managers
{
    internal class BuySellAgent
    {
        public static BuySellAgent Instance { get; private set; }

        public List<BuySellItemConfig> SellConfigs { get { return sellItems; } }
        public List<BuySellItemConfig> BuyConfigs { get { return buyItems; } }

        private List<BuySellItemConfig> sellItems;
        private List<BuySellItemConfig> buyItems;

        private readonly Dictionary<uint, VendorSellInfo> sellPackets = new Dictionary<uint, VendorSellInfo>();
        public static void Load()
        {
            Instance = new BuySellAgent();

            try
            {
                string savePath = Path.Combine(ProfileManager.ProfilePath, "SellAgentConfig.json");
                if (File.Exists(savePath))
                {
                    Instance.sellItems = JsonSerializer.Deserialize<List<BuySellItemConfig>>(File.ReadAllText(savePath));
                }

                savePath = Path.Combine(ProfileManager.ProfilePath, "BuyAgentConfig.json");
                if (File.Exists(savePath))
                {
                    Instance.buyItems = JsonSerializer.Deserialize<List<BuySellItemConfig>>(File.ReadAllText(savePath));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading BuySellAgent config: {ex}");
            }
        }

        public void DeleteConfig(BuySellItemConfig config)
        {
            if(config == null) return;
            
            SellConfigs?.Remove(config);
            BuyConfigs?.Remove(config);
        }

        public BuySellItemConfig NewSellConfig()
        {
            var r = new BuySellItemConfig();

            sellItems ??= new List<BuySellItemConfig>();

            sellItems.Add(r);
            return r;
        }
        public BuySellItemConfig NewBuyConfig()
        {
            var r = new BuySellItemConfig();

            buyItems ??= new List<BuySellItemConfig>();

            buyItems.Add(r);
            return r;
        }

        public static void Unload()
        {
            if (Instance != null)
            {
                if (Instance.sellItems != null)
                {
                    string savePath = Path.Combine(ProfileManager.ProfilePath, "SellAgentConfig.json");
                    File.WriteAllText(savePath, JsonSerializer.Serialize(Instance.sellItems));
                }

                if (Instance.buyItems != null)
                {
                    string savePath = Path.Combine(ProfileManager.ProfilePath, "BuyAgentConfig.json");
                    File.WriteAllText(savePath, JsonSerializer.Serialize(Instance.buyItems));
                }
            }

            Instance = null;
        }

        public void HandleBuyPacket(List<Item> items, uint shopSerial)
        {
            if (!ProfileManager.CurrentProfile.BuyAgentEnabled) return;

            if (buyItems == null) return;

            List<Tuple<uint, ushort>> buyList = new List<Tuple<uint, ushort>>();

            foreach (var buyConfigEntry in buyItems)
            {
                if (!buyConfigEntry.Enabled) continue;

                ushort current_count = 0;

                foreach (var item in items)
                {
                    if (!buyConfigEntry.IsMatch(item.Graphic, item.Hue)) continue;

                    if (current_count >= buyConfigEntry.MaxAmount) continue;

                    //Made it here, add to buy list
                    if (current_count + item.Amount < buyConfigEntry.MaxAmount)
                    {
                        buyList.Add(new Tuple<uint, ushort>(item.Serial, item.Amount));
                        current_count += item.Amount;
                    }
                    else
                    {
                        ushort remainingAmount = (ushort)(buyConfigEntry.MaxAmount - current_count);
                        if (remainingAmount > 0)
                        {
                            buyList.Add(new Tuple<uint, ushort>(item.Serial, remainingAmount));
                            current_count += remainingAmount;
                        }
                    }
                }
            }

            if (buyList.Count == 0) return;

            NetClient.Socket.Send_BuyRequest(shopSerial, buyList.ToArray());
            UIManager.GetGump(shopSerial)?.Dispose();
        }

        public void HandleSellPacket(uint vendorSerial, uint serial, ushort graphic, ushort hue, ushort amount, uint price)
        {
            if (!ProfileManager.CurrentProfile.SellAgentEnabled) return;

            if (!sellPackets.ContainsKey(vendorSerial))
                sellPackets.Add(vendorSerial, new VendorSellInfo());

            sellPackets[vendorSerial].HandleSellPacketItem(serial, graphic, hue, amount, price);
        }

        public void HandleSellPacketFinished(uint vendorSerial)
        {
            if (!ProfileManager.CurrentProfile.SellAgentEnabled) return;

            if (sellItems == null)
            {
                sellPackets.Remove(vendorSerial);
                return;
            }

            List<Tuple<uint, ushort>> sellList = new List<Tuple<uint, ushort>>();

            foreach (var sellConfig in sellItems)
            {
                if (!sellConfig.Enabled) continue;

                ushort current_count = 0;
                foreach (var item in sellPackets[vendorSerial].AvailableItems)
                {
                    if (!sellConfig.IsMatch(item.Graphic, item.Hue)) continue;

                    if (current_count >= sellConfig.MaxAmount) continue;

                    //Made it here, add to sell list
                    if (current_count + item.Amount < sellConfig.MaxAmount)
                    {
                        sellList.Add(new Tuple<uint, ushort>(item.Serial, item.Amount));
                        current_count += item.Amount;
                    }
                    else
                    {
                        ushort remainingAmount = (ushort)(sellConfig.MaxAmount - current_count);
                        if (remainingAmount > 0)
                        {
                            sellList.Add(new Tuple<uint, ushort>(item.Serial, remainingAmount));
                            current_count += remainingAmount;
                        }
                    }
                }
            }
            sellPackets.Remove(vendorSerial);

            if (sellList.Count == 0) return;

            NetClient.Socket.Send_SellRequest(vendorSerial, sellList.ToArray());
            UIManager.GetGump(vendorSerial)?.Dispose();
        }
    }

    internal class BuySellItemConfig
    {
        public ushort Graphic { get; set; }
        public ushort Hue { get; set; } = ushort.MaxValue;
        public ushort MaxAmount { get; set; } = ushort.MaxValue;
        public bool Enabled { get; set; } = true;

        public bool IsMatch(ushort graphic, ushort hue)
        {
            return graphic == Graphic && (hue == Hue || Hue == ushort.MaxValue);
        }
    }

    internal class VendorSellInfo
    {
        public List<VendorSellItemData> AvailableItems { get; set; } = new List<VendorSellItemData>();
        public void HandleSellPacketItem(uint serial, ushort graphic, ushort hue, ushort amount, uint price)
        {
            AvailableItems.Add(new VendorSellItemData(serial, graphic, hue, amount, price));
        }
    }

    internal class VendorSellItemData
    {
        public uint Serial;
        public ushort Graphic;
        public ushort Hue;
        public ushort Amount;
        public uint Price;

        public VendorSellItemData(uint serial, ushort graphic, ushort hue, ushort amount, uint price)
        {
            Serial = serial;
            Graphic = graphic;
            Hue = hue;
            Amount = amount;
            Price = price;
        }
    }
}
