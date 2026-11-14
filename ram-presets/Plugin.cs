// RAM Quick Presets Host 3.0 (experimental, host-only)
// Requires R.A.M. 1.0.16, BepInEx 5. UI hooks and game logic use reflection.
// No modification to the original R.A.M. DLL, no native save-file writing.
using BepInEx;
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Xml.Serialization;

namespace RAMQuickPresets {
[BepInPlugin("es.ram.quickpresets.host", "R.A.M. Quick Presets Host", "3.1.0")]
[BepInDependency("proferabg.REPO.RepoAdminMenu", BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BaseUnityPlugin {
    const int SLOTS = 5, MONEY_LIMIT = 1000000000, ITEM_LIMIT = 150;
    const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly string[] Globals = {"infiniteMoney","noBreak","noBatteryDrain","noTraps","weakEnemies","deafEnemies","blindEnemies","freezeEnemyAI","boomhammer","friendlyDuck","disableShopKeeper","useShopUpgrades"};
    static readonly string[] Flags = {"godModePlayers","noDeathPlayers","infiniteStaminaPlayers","noTargetPlayers","noTumblePlayers","forcedTumble"};
    Type menu, semi, statsType, runType, punType, settingsType, upgradesType, itemType, mapType;
    bool registered;
    string status = "Solo host. Los archivos se guardan fuera de las partidas originales.";
    string Folder { get { return Path.Combine(Paths.ConfigPath, "RAMQuickPresetsHost"); } }

    static Type FindType(string name) {
        return AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType(name,false)).FirstOrDefault(t=>t!=null);
    }
    static object Get(object obj, string name) {
        if (obj == null) return null;
        Type type = obj as Type ?? obj.GetType();
        object target = obj is Type ? null : obj;
        var f = type.GetField(name, BF); if (f!=null) return f.GetValue(target);
        var p = type.GetProperty(name, BF); if (p!=null) return p.GetValue(target,null);
        return null;
    }
    static void Put(object obj, string name, object value) {
        if (obj==null) throw new InvalidOperationException("Missing object for "+name);
        Type t = obj as Type ?? obj.GetType();
        object target = obj is Type ? null : obj;
        var f = t.GetField(name, BF); if(f!=null){f.SetValue(target,value);return;}
        var p = t.GetProperty(name, BF); if(p!=null){p.SetValue(target,value,null);return;}
        throw new MissingMemberException(t.FullName, name);
    }
    static object Call(object targetOrType, string name, params object[] args) {
        Type t = targetOrType as Type ?? targetOrType.GetType();
        object target = targetOrType is Type ? null : targetOrType;
        var all = t.GetMethods(BF).Where(m=>m.Name==name && m.GetParameters().Length==args.Length).ToList();
        MethodInfo m = all.FirstOrDefault(x => {
            var ps=x.GetParameters();
            for(int i=0;i<ps.Length;i++) if(args[i]!=null && !ps[i].ParameterType.IsInstanceOfType(args[i])) return false;
            return true;
        }) ?? all.FirstOrDefault();
        if(m==null) throw new MissingMethodException(t.FullName,name);
        return m.Invoke(target,args);
    }
    static IEnumerable Iter(object obj) { return obj as IEnumerable ?? new object[0]; }
    static string Txt(object v) { return v==null ? "" : v.ToString(); }
    static int Num(object v) { int a; return int.TryParse(Txt(v), out a) ? a : 0; }
    object Stats { get { return Get(statsType,"instance"); } }
    object Run { get { return Get(runType,"instance"); } }
    object Pun { get { return Get(punType,"instance"); } }
    object ConfigSettings { get { return Get(settingsType,"instance"); } }

    void Awake() {
        try {
            menu=FindType("RepoAdminMenu.Menu"); semi=FindType("SemiFunc");
            statsType=FindType("StatsManager"); runType=FindType("RunManager");
            punType=FindType("PunManager"); settingsType=FindType("RepoAdminMenu.Settings");
            upgradesType=FindType("RepoAdminMenu.Utils.UpgradeUtil");
            itemType=FindType("RepoAdminMenu.Utils.ItemUtil");
            mapType=FindType("RepoAdminMenu.Utils.MapUtil");
            if(new[]{menu,semi,statsType,runType,punType,settingsType,upgradesType,itemType,mapType}.Any(t=>t==null))
                throw new Exception("One or more R.A.M. / R.E.P.O. types are missing");
            var method = menu.GetMethod("addMenuPreCallback",BF);
            var dType = method.GetParameters()[1].ParameterType;
            Type pageType = dType.GetGenericArguments()[1];
            var a=Expression.Parameter(typeof(string),"menuName");
            var b=Expression.Parameter(pageType,"page");
            MethodInfo handler=GetType().GetMethod("MenuHook",BindingFlags.NonPublic|BindingFlags.Instance);
            var body=Expression.Call(Expression.Constant(this),handler,a,Expression.Convert(b,typeof(object)));
            Delegate d=Expression.Lambda(dType,body,a,b).Compile();
            method.Invoke(null,new object[]{"RAMQuickPresetsHost",d});
            Logger.LogInfo("Quick Presets host hooked into R.A.M. 1.0.16");
        } catch(Exception ex){ Logger.LogError("Quick Presets initialization: "+ex); }
    }

    void MenuHook(string menuName, object page) {
        try {
            if(menuName!="mainmenu") return;
            if(!registered){
                Call(menu,"registerMenu","ramquick-presets",new Action(OpenPresets));
                registered=true;
            }
            AddButton(page,"Guardados rapidos (5 ranuras)",()=>{ Close(page); OpenPresets(); });
        } catch(Exception ex){Logger.LogError("Menu hook: "+ex);}
    }
    void AddButton(object page,string caption,Action click){ Call(menu,"addButton",page,caption,click); }
    void AddLabel(object page,string caption){ Call(menu,"addLabel",page,caption); }
    void Close(object page){ Call(menu,"closePage",page); }
    void Show(object page,string name){ Call(menu,"openPage",page,name); }
    object NewPage(string title,string id){return Call(menu,"createMenu",title,id,"mainmenu");}
    void Back(object page){Close(page);Call(menu,"openMainMenu");}
    bool IsHost(){
        try {
            return !(bool)Call(semi,"MenuLevel") && (bool)Call(semi,"IsMasterClientOrSingleplayer")
               && Stats!=null && Run!=null && Pun!=null;
        }catch{return false;}
    }
    void OpenPresets() {
        if(!IsHost()) { Logger.LogWarning("Presets blocked (not host or not in run)");return; }
        try {
            object page=NewPage("R.A.M. - Guardados rapidos","ramquick-presets");
            AddLabel(page,status);
            AddLabel(page,"Nivel / dinero / perks / mejoras / mapas / compras.");
            AddLabel(page,"Objetos: se compara el registro de compras con el equipo que hay en la escena.");
            for(int i=1;i<=SLOTS;i++){
                int slot=i; bool exists=File.Exists(SlotFile(slot));
                AddLabel(page,"RANURA "+slot+": "+Summary(slot));
                AddButton(page,exists ? "Sobrescribir "+slot : "Guardar "+slot,()=>{
                    if(exists) Confirm(page,"Sobrescribir "+slot+"?",()=>Save(slot));
                    else {Save(slot);Close(page);OpenPresets();}
                });
                if(exists) {
                    AddButton(page,"Cargar "+slot,()=>Confirm(page,"Cargar "+slot+" y recuperar compras?",()=>Load(slot)));
                    AddButton(page,"Borrar "+slot,()=>Confirm(page,"Borrar "+slot+"?",()=>Delete(slot)));
                }
            }
            AddLabel(page,"Dinero (valor R.A.M. sin multiplicar): "+Money());
            AddButton(page,"+1.000.000",()=>{SetMoney(Math.Min(MONEY_LIMIT,Money()+1000000));Close(page);OpenPresets();});
            AddButton(page,"Establecer 1.000.000",()=>{SetMoney(1000000);Close(page);OpenPresets();});
            AddButton(page,"Volver",()=>Back(page));
            Show(page,"ramquick-presets");
        }catch(Exception ex){ Logger.LogError("Open presets: "+ex); }
    }
    void Confirm(object from,string prompt,Action yes) {
        Close(from);
        object page=NewPage("R.A.M. - Confirmar","ramquick-confirm");
        AddLabel(page,prompt);
        AddButton(page,"SI",()=>{Close(page);try{yes();}catch(Exception ex){Logger.LogError(ex);status="ERROR: revisar LogOutput.log";}OpenPresets();});
        AddButton(page,"Cancelar",()=>{Close(page);OpenPresets();});
        Show(page,"ramquick-confirm");
    }

    string SlotFile(int n){if(n<1||n>SLOTS)throw new ArgumentOutOfRangeException("n");return Path.Combine(Folder,"ranura"+n+".xml");}
    int Money() {
        IDictionary d=Get(Stats,"runStats") as IDictionary;
        return d!=null && d.Contains("currency") ? Num(d["currency"]) : 0;
    }
    void SetMoney(int n){if(IsHost()) Call(Pun,"SetRunStatSet","currency",Math.Max(0,Math.Min(MONEY_LIMIT,n)));}
    object PlayerId(object avatar){return Call(semi,"PlayerGetSteamID",avatar);}
    string PlayerName(object avatar){return Txt(Call(semi,"PlayerGetName",avatar));}
    IEnumerable AllPlayers(){return Iter(Call(semi,"PlayerGetAll"));}
    string Summary(int n){
        if(!File.Exists(SlotFile(n))) return "vacia";
        try{var s=Read(n);return "nivel "+s.Level+" | $"+s.Money+" | "+s.Players.Count+" jugadores | "+s.Items.Count+" tipos de compras";}
        catch{return "archivo dañado o incompatible";}
    }
    Slot Read(int n){
        using(var fs=File.OpenRead(SlotFile(n))){
            var s=(Slot)new XmlSerializer(typeof(Slot)).Deserialize(fs);
            if(s==null||s.Schema!=3||s.Money<0||s.Money>MONEY_LIMIT||s.Level<0||s.Level>100000)throw new InvalidDataException("Bad snapshot");
            return s;
        }
    }
    static void WriteXml(string path,Slot data){
        string tmp=path+".tmp";
        using(var stream=new FileStream(tmp,FileMode.Create,FileAccess.Write,FileShare.None)){
            new XmlSerializer(typeof(Slot)).Serialize(stream,data);stream.Flush(true);
        }
        if(File.Exists(path)) File.Replace(tmp,path,path+".bak",true);
        else File.Move(tmp,path);
    }
    void Save(int n) {
        if(!IsHost())return;
        try{Directory.CreateDirectory(Folder);var s=Capture();WriteXml(SlotFile(n),s);
            status="Guardado "+n+": nivel "+s.Level+", dinero "+s.Money+", "+s.Items.Sum(p=>p.Count)+" compras.";
            Logger.LogInfo(status);
        }catch(Exception ex){status="Error guardando "+n;Logger.LogError(ex);}
    }
    void Delete(int n) {
        if(!IsHost())return;
        try{var p=SlotFile(n);if(File.Exists(p)){File.Copy(p,p+".deleted.bak",true);File.Delete(p);}status="Ranura "+n+" borrada (copia .deleted.bak)";}
        catch(Exception ex){status="Error al borrar";Logger.LogError(ex);}
    }
    Slot Capture() {
        var s=new Slot{SavedUtc=DateTime.UtcNow.ToString("u"),Level=Num(Get(Run,"levelsCompleted")),Money=Money()};
        IDictionary bought=Get(Stats,"itemsPurchased") as IDictionary;
        if(bought!=null)foreach(DictionaryEntry kv in bought){
            int cnt=Num(kv.Value);if(cnt>0&&!string.IsNullOrEmpty(Txt(kv.Key)))
                s.Items.Add(new Pair{Key=Txt(kv.Key),Count=cnt});
        }
        object settings=ConfigSettings;
        foreach(string key in Globals){
            object v=Get(settings,key);if(v is bool)s.Globals.Add(new Pair{Key=key,Count=(bool)v?1:0});
        }
        foreach(object av in AllPlayers()){
            if(av==null)continue;
            string id=Txt(PlayerId(av));
            var p=new PlayerState{SteamId=id,Name=PlayerName(av)};
            foreach(object kv in Iter(Get(Stats,"dictionaryOfDictionaries"))){
                string key=Txt(Get(kv,"Key"));if(!key.StartsWith("playerUpgrade",StringComparison.Ordinal))continue;
                var d=Get(kv,"Value") as IDictionary;
                if(d!=null && d.Contains(id))p.Upgrades.Add(new Pair{Key=key,Count=Num(d[id])});
            }
            foreach(object kv in Iter(Call(upgradesType,"GetModUpgrades"))){
                var up=Get(kv,"Value");string key=Txt(Get(up,"UpgradeId"));
                if(!string.IsNullOrEmpty(key)) {
                    try{p.ModUpgrades.Add(new Pair{Key=key,Count=Num(Call(up,"GetLevel",av))});}catch{}
                }
            }
            foreach(string key in Flags){
                var list=Get(settings,key) as IList;
                var dict=Get(settings,key) as IDictionary;
                bool enabled=list!=null?list.Contains(id):dict!=null&&dict.Contains(id);
                p.Flags.Add(new Pair{Key=key,Count=enabled?1:0});
            }
            s.Players.Add(p);
        }
        try{
            foreach(object kv in Iter(Call(mapType,"getMaps"))){
                string name=Txt(Get(kv,"Key"));object map=Get(kv,"Value");
                if(!string.IsNullOrEmpty(name))s.Maps.Add(new Pair{Key=name,Count=(bool)Call(mapType,"isMapEnabled",map)?1:0});
            }
        }catch(Exception ex){Logger.LogWarning("Maps capture: "+ex.Message);}
        return s;
    }
    void Load(int n){
        if(!IsHost())return;
        try{
            var save=Read(n);
            Directory.CreateDirectory(Folder);
            WriteXml(Path.Combine(Folder,"antes_de_cargar.xml"),Capture());
            Put(Run,"levelsCompleted",save.Level);
            SetMoney(save.Money);
            object settings=ConfigSettings;
            foreach(var kv in save.Globals)if(Globals.Contains(kv.Key))Put(settings,kv.Key,kv.Count>0);
            var live=AllPlayers().Cast<object>().Where(x=>x!=null).ToList();
            var used=new HashSet<object>();
            int restored=0,unmatched=0;
            foreach(var p in save.Players){
                var matches=live.Where(av=>!used.Contains(av)&&Txt(PlayerId(av))==p.SteamId&&!string.IsNullOrEmpty(p.SteamId)).ToList();
                if(matches.Count!=1)matches=live.Where(av=>!used.Contains(av)&&PlayerName(av)==p.Name&&!string.IsNullOrEmpty(p.Name)).ToList();
                if(matches.Count!=1){unmatched++;Logger.LogWarning("Player not found: "+p.Name);continue;}
                object avMatch=matches[0];used.Add(avMatch);restored++;
                foreach(object pair in Iter(Call(upgradesType,"GetGameUpgrades"))){
                    object up=Get(pair,"Value");string key=Txt(Call(up,"GetID"));
                    var val=p.Upgrades.FirstOrDefault(x=>x.Key==key);
                    if(val!=null && val.Count>=0 && val.Count<=500)try{Call(up,"Upgrade",avMatch,val.Count);}catch(Exception ex){Logger.LogWarning("Upgrade: "+ex.Message);}
                }
                foreach(object pair in Iter(Call(upgradesType,"GetModUpgrades"))){
                    object up=Get(pair,"Value");string key=Txt(Get(up,"UpgradeId"));
                    var val=p.ModUpgrades.FirstOrDefault(x=>x.Key==key);
                    if(val!=null && val.Count>=0 && val.Count<=500)try{Call(upgradesType,"UpgradeLevel",up,avMatch,val.Count);}catch(Exception ex){Logger.LogWarning("Mod upgrade: "+ex.Message);}
                }
                string id=Txt(PlayerId(avMatch));
                foreach(var flag in p.Flags)if(Flags.Contains(flag.Key)){
                    var list=Get(settings,flag.Key) as IList;
                    if(list!=null){
                        while(list.Contains(id))list.Remove(id);
                        if(flag.Count>0)list.Add(id);
                    }else{
                        var dic=Get(settings,flag.Key) as IDictionary;
                        if(dic!=null){if(dic.Contains(id))dic.Remove(id);if(flag.Count>0)dic[id]=(long)0;}
                    }
                }
            }
            foreach(object pair in Iter(Call(mapType,"getMaps"))){
                string key=Txt(Get(pair,"Key")); var saved=save.Maps.FirstOrDefault(x=>x.Key==key);
                if(saved!=null)try{Call(mapType,"setMapEnabled",Get(pair,"Value"),saved.Count>0);}catch{}
            }
            try{Call(settingsType,"UpdateClients");}catch(Exception ex){Logger.LogWarning("Settings sync: "+ex.Message);}
            int spawned=RestoreBought(save);
            status="Cargada ranura "+n+": "+restored+" jugadores, "+spawned+" apariciones solicitadas; "+unmatched+" ausentes.";
            Logger.LogInfo(status);
        }catch(Exception ex){status="Error cargando "+n+". Ver LogOutput.log";Logger.LogError(ex);}
    }
    // Scene counts come from ItemManager.spawnedItems, not from the purchase ledger.
    // This prevents loading the same preset repeatedly from duplicating owned items.
    Dictionary<string,int> SceneItemCounts(IDictionary registry) {
        Type manager=FindType("ItemManager");
        object instance=manager==null ? null : Get(manager,"instance");
        object raw=instance==null ? null : Get(instance,"spawnedItems");
        if(raw==null) return null; // Fail closed: never blindly spawn items.
        var counts=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(object attrs in Iter(raw)) {
            try {
                if(attrs==null)continue;
                var unity=attrs as UnityEngine.Object;
                if(unity!=null && unity==null)continue;
                if(Get(attrs,"shopItem") is bool && (bool)Get(attrs,"shopItem"))continue;
                object item=Get(attrs,"item");
                string key=Txt(Get(item,"name"));
                if(string.IsNullOrEmpty(key)||!registry.Contains(key))continue;
                counts[key]=counts.ContainsKey(key)? counts[key]+1 : 1;
            }catch(Exception e){Logger.LogWarning("Skipping invalid item in scene: "+e.Message);}
        }
        return counts;
    }

    int RestoreBought(Slot saved){
        IDictionary bought=Get(Stats,"itemsPurchased") as IDictionary;
        IDictionary registry=Get(Stats,"itemDictionary") as IDictionary;
        if(bought==null||registry==null){Logger.LogWarning("Purchase ledger or item registry not found.");return 0;}
        var scene=SceneItemCounts(registry);
        if(scene==null) {Logger.LogWarning("ItemManager.spawnedItems unavailable, avoiding possible duplicates.");return 0;}
        var totals=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(var kv in saved.Items)if(kv!=null&&!string.IsNullOrEmpty(kv.Key)&&kv.Count>0)
            totals[kv.Key]=Math.Min(ITEM_LIMIT,kv.Count);
        bool inShop=false;
        try{inShop=(bool)Call(semi,"RunIsShop");}catch{}
        object settings=ConfigSettings;int prevCount=Num(Get(settings,"spawnerCount"));
        int attempted=0,missingTypes=0;
        try {
            Put(settings,"spawnerCount",1);
            foreach(var pair in totals) {
                string id=pair.Key;int owned=pair.Value;
                object def=registry.Contains(id)?registry[id]:null;
                string resolved=id;
                if(def==null)foreach(DictionaryEntry entry in registry) {
                    object v=entry.Value;string name=Txt(Get(v,"name"));string shortName=Txt(Get(v,"itemName"));
                    if(string.Equals(Txt(entry.Key),id,StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name,id,StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(shortName,id,StringComparison.OrdinalIgnoreCase)) {
                        def=v;resolved=Txt(entry.Key);break;
                    }
                }
                if(def==null){missingTypes++;Logger.LogWarning("Purchased item not registered: "+id);continue;}
                int currentLedger=bought.Contains(resolved)?Num(bought[resolved]):0;
                // Restore ownership without deleting any newer purchases made in the current run.
                if(bought.Contains(resolved))bought[resolved]=Math.Max(owned,currentLedger);
                else bought.Add(resolved,owned);
                if(inShop)continue; // Shop items cannot be distinguished from goods on sale.
                int live=scene.ContainsKey(resolved)?scene[resolved]:0;
                int missing=Math.Max(0,owned-live);
                if(missing<=0)continue;
                // Limit physics pile-ups and pathological/corrupt snapshots.
                int batch=Math.Min(10,Math.Min(missing,ITEM_LIMIT-attempted));
                if(batch<=0)break;
                try{
                    Put(settings,"spawnerCount",batch);
                    Call(itemType,"spawnItem",def); // R.A.M. uses the host's native REPOLib item spawning.
                    attempted+=batch;
                    Logger.LogInfo("Restoring "+resolved+" x"+batch+" ("+live+" already present)");
                }catch(Exception ex){Logger.LogWarning("Could not spawn "+resolved+": "+ex.Message);}
                finally {Put(settings,"spawnerCount",1);}
                if(batch<missing)Logger.LogWarning("Restore limited to "+batch+"/"+missing+" for "+resolved);
            }
        } finally {if(settings!=null)Put(settings,"spawnerCount",prevCount);}
        if(missingTypes>0)Logger.LogWarning(missingTypes+" item types were not registered, e.g. missing content mods.");
        if(inShop)Logger.LogInfo("Purchase ownership restored; physical spawning skipped inside shop.");
        return attempted; // attempted spawns, not confirmed network instances
    }

}
[Serializable] public class Pair { public string Key; public int Count; }
[Serializable] public class PlayerState {
    public string SteamId,Name;
    public List<Pair> Upgrades=new List<Pair>();
    public List<Pair> ModUpgrades=new List<Pair>();
    public List<Pair> Flags=new List<Pair>();
}
[Serializable] public class Slot {
    public int Schema=3;
    public string SavedUtc;
    public int Level,Money;
    public List<Pair> Items=new List<Pair>();
    public List<Pair> Globals=new List<Pair>();
    public List<Pair> Maps=new List<Pair>();
    public List<PlayerState> Players=new List<PlayerState>();
}
}