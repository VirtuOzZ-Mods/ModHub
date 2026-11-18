#include <Windows.h>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <type_traits>
#include <algorithm>
#include "core.h"

// Public ScriptHookV ABI declarations only. No SDK implementation is bundled.
__declspec(dllimport) void scriptRegister(HMODULE, void(*)());
__declspec(dllimport) void scriptUnregister(HMODULE);
__declspec(dllimport) void scriptWait(DWORD);
__declspec(dllimport) void nativeInit(std::uint64_t);
__declspec(dllimport) void nativePush64(std::uint64_t);
__declspec(dllimport) std::uint64_t* nativeCall();

namespace {
// Hashes/signatures checked against citizenfx/natives; see NATIVE-SOURCES.md.
template<class T> std::uint64_t argument(T value) {
 if constexpr(std::is_pointer_v<T>) return reinterpret_cast<std::uint64_t>(value);
 else if constexpr(std::is_same_v<T,float>) { std::uint32_t bits; std::memcpy(&bits,&value,4); return bits; }
 else return static_cast<std::uint64_t>(value);
}
template<class R=void,class... A> R invoke(std::uint64_t hash,A... args) {
 nativeInit(hash); (nativePush64(argument(args)),...); auto result=nativeCall();
 if constexpr(!std::is_void_v<R>) { R value{}; if(result) std::memcpy(&value,result,sizeof(R)); return value; }
}
constexpr std::uint64_t PlayerId=0x4F8644AF03D0E0D6, PlayerPedId=0xD80958FC74E988A6;
constexpr std::uint64_t Playing=0x5E9564D8246B909A, Dead=0x5F9532F3B5CC2551;
constexpr std::uint64_t Online=0x9DE624D2FC4B603F, Session=0xD83C2B94E7508980;
constexpr std::uint64_t CdImage=0x35B9E0803292B641, Valid=0xC0296A2EDF545E92, PedModel=0x75816577FEA6DAD5;
constexpr std::uint64_t Request=0x963D27A58DF860AC, Loaded=0x98A4EB5D89A0C952, Release=0xE532F5D78798DAAB;
constexpr std::uint64_t Change=0x00A1CADD00108836, Defaults=0x45EEE61580806D63;
constexpr std::uint64_t DisableAll=0x5F4B6931816E599B, Disable=0xFE99B66D079CF6BC;

// ScriptHookV runs scripts on fibers with a small stack: never put large buffers on the stack here
// (a 64 KB wchar_t array caused STATUS_STACK_OVERFLOW 0xC00000FD at script start). Heap buffer, grown on demand.
std::wstring modulePath(HMODULE module) {
 std::wstring buffer(MAX_PATH,L'\0');
 for(;;) {
  DWORD length=GetModuleFileNameW(module,buffer.data(),static_cast<DWORD>(buffer.size()));
  if(!length) return {};
  if(length<buffer.size()) { buffer.resize(length); return buffer; }
  if(buffer.size()>=32768) return {};
  buffer.resize(buffer.size()*2);
 }
}
std::filesystem::path gameRoot() {
 auto exe=modulePath(nullptr);
 if(exe.empty()) throw std::runtime_error("Cannot resolve game folder");
 return std::filesystem::path(exe).parent_path();
}
std::string read(const std::filesystem::path& path) {
 std::ifstream file(path,std::ios::binary|std::ios::ate); if(!file) return {};
 auto length=file.tellg(); if(length<0||length>1024*1024) return {};
 std::string text(static_cast<size_t>(length),'\0'); file.seekg(0);
 if(!text.empty()&&!file.read(text.data(),text.size())) return {};
 return text;
}
// ModHub is portable and leaves no files outside its own folder, the game folder and its service folder.
// The game process cannot know where ModHub lives, so the selector keeps no log file.
void log(const std::filesystem::path&,const std::string&) noexcept {}
bool focused() {
 DWORD process=0; GetWindowThreadProcessId(GetForegroundWindow(),&process);
 return process==GetCurrentProcessId();
}
bool playable() {
 if(invoke<int>(Online)||invoke<int>(Session)) return false;
 const auto player=invoke<int>(PlayerId), ped=invoke<int>(PlayerPedId);
 return ped && invoke<int>(Playing,player) && !invoke<int>(Dead,ped,false);
}
std::uint32_t hashModel(const std::string& text) {
 std::uint32_t hash=0;
 for(unsigned char c:gmm::lower(text)) {hash+=c;hash+=hash<<10;hash^=hash>>6;}
 hash+=hash<<3;hash^=hash>>11;hash+=hash<<15;return hash;
}
// Panel flush with the top-left screen corner (normalized screen coordinates, 0..1), ModHub colours.
// Width follows the longest visible name so text is never clipped; very long names are shortened by measured width.
constexpr float HeaderH=0.054f, AccentH=0.003f, RowH=0.031f, FooterH=0.028f, PadX=0.008f, BarW=0.0035f;
constexpr float MinW=0.18f, MaxW=0.42f, TitleScale=0.46f, RowScale=0.31f, HintScale=0.25f;
constexpr int TitleFont=4, RowFont=0;
constexpr size_t VisibleRows=12;
constexpr std::uint64_t Font=0x66E0276CC5F6B9DA, Scale=0x07C837F9A01C34C9, Colour=0xBE6B23FFA53FB442;
constexpr std::uint64_t Justify=0x4E096588B13FFECA, Wrap=0x63145D9C883A1A70, Component=0x6C188BE134E074AA;
constexpr std::uint64_t BeginText=0x25FBB336DF1804CB, EndText=0xCD015E5BB0D96A57;
constexpr std::uint64_t BeginWidth=0x54CE8AC98E120CAB, EndWidth=0x85F061DA64ED2F67, CharHeight=0xDB88A37483346780;
constexpr std::uint64_t ResetAlign=0xE3A3DB414A373DAB, Rect=0x3A618A217E5154F0;
// Native text components accept at most 99 characters; cut on UTF-8 boundaries.
std::string clip(std::string value,size_t maxBytes) {
 if(value.size()<=maxBytes) return value;
 size_t end=maxBytes;while(end&&((unsigned char)value[end]&0xc0)==0x80)--end;
 return value.substr(0,end);
}
float width(const std::string& value,float scale,int font) {
 invoke(Font,font); invoke(Scale,0.0f,scale);
 invoke(BeginWidth,"STRING"); invoke(Component,clip(value,96).c_str());
 return invoke<float>(EndWidth,true);
}
// Shorten with ".." until the measured width fits.
std::string fit(std::string value,float scale,int font,float room) {
 value=clip(value,96);
 if(width(value,scale,font)<=room) return value;
 while(!value.empty()) {
  size_t end=value.size()-1;while(end&&((unsigned char)value[end]&0xc0)==0x80)--end;
  value.resize(end);
  if(width(value+"..",scale,font)<=room) return value+"..";
 }
 return "..";
}
// Text vertically centred in a band starting at `top`; right=true aligns to the panel's right padding.
void text(float x,float top,float height,const std::string& value,float scale,int font,int red,int green,int blue,float panelW=0,bool right=false) {
 const float glyph=invoke<float>(CharHeight,scale,font);
 invoke(Font,font); invoke(Scale,0.0f,scale); invoke(Colour,red,green,blue,255);
 if(right) {invoke(Justify,2); invoke(Wrap,0.0f,panelW-PadX);}
 invoke(BeginText,"STRING"); invoke(Component,clip(value,96).c_str());
 invoke(EndText,right?0.0f:x,top+(height-glyph)/2-0.002f,0);
}
// Rectangle by its left/top edge and size.
void rect(float left,float top,float w,float h,int red,int green,int blue,int alpha=255) {
 invoke(Rect,left+w/2,top+h/2,w,h,red,green,blue,alpha,false);
}
// ---------- Customization: outfit slots and props of the current ped (natives verified against citizenfx/natives) ----------
constexpr std::uint64_t EntityModel=0x9F47B058362C84B5;
constexpr std::uint64_t NumDrawables=0x27561561732A7842, NumTextures=0x8F7156A3142A6BAD;
constexpr std::uint64_t GetDrawable=0x67F3780DD425D4FC, GetTexture=0x04A355E041E004E6, GetPalette=0xE3DD5F2A84B42281;
constexpr std::uint64_t SetComponent=0x262B14F48D29DE80, ValidComponent=0xE825F6B6CEA7671D;
constexpr std::uint64_t NumPropDrawables=0x5FAF9754E789FB47, NumPropTextures=0xA6E7F1CEB523E171;
constexpr std::uint64_t GetProp=0x898CC20EA75BACD8, GetPropTexture=0xE131A28626F81AB2, SetProp=0x93376B65A266EB5F;
constexpr std::uint64_t ClearProp=0x0943E5B8E078E76E, ClearAllProps=0xCD8A7537A9B52F06;
constexpr const char* CompNames[12]={"Head","Mask / beard","Hair","Torso","Legs","Bag","Shoes","Accessory","Undershirt","Armor","Decals","Top"};
constexpr int PropSlots[5]={0,1,2,6,7};
constexpr const char* PropNames[8]={"Hat","Glasses","Ears","","","","Watch","Bracelet"};

// Saved looks live in a folder ModHub creates at install and opens for writing; the rest of the game stays read-only.
std::filesystem::path looksPath() { return gameRoot()/L"GMMPedSelector"/L"looks.txt"; }

enum class RowKind { Comp, Prop, Save, Reset, Back };
struct Row { RowKind kind; int slot; };

std::vector<Row> customRows(int ped) {
 std::vector<Row> rows;
 for(int c=0;c<12;++c) {
  const int drawables=invoke<int>(NumDrawables,ped,c);
  const int textures=invoke<int>(NumTextures,ped,c,invoke<int>(GetDrawable,ped,c));
  if(drawables>1||textures>1) rows.push_back({RowKind::Comp,c});
 }
 for(int p:PropSlots) if(invoke<int>(NumPropDrawables,ped,p)>0) rows.push_back({RowKind::Prop,p});
 rows.push_back({RowKind::Save,0}); rows.push_back({RowKind::Reset,0}); rows.push_back({RowKind::Back,0});
 return rows;
}

std::string rowLabel(const Row& row) {
 switch(row.kind) {
  case RowKind::Comp: return CompNames[row.slot];
  case RowKind::Prop: return PropNames[row.slot];
  case RowKind::Save: return "Save look";
  case RowKind::Reset: return "Reset to default";
  default: return "< Back";
 }
}

std::string rowValue(int ped,const Row& row) {
 if(row.kind==RowKind::Comp) {
  const int d=invoke<int>(GetDrawable,ped,row.slot), n=invoke<int>(NumDrawables,ped,row.slot);
  const int t=invoke<int>(GetTexture,ped,row.slot), tn=invoke<int>(NumTextures,ped,row.slot,d);
  return "< "+std::to_string(d+1)+"/"+std::to_string(n)+" >"+(tn>1?"  col "+std::to_string(t+1)+"/"+std::to_string(tn):"");
 }
 if(row.kind==RowKind::Prop) {
  const int d=invoke<int>(GetProp,ped,row.slot), n=invoke<int>(NumPropDrawables,ped,row.slot);
  if(d<0) return "< none >";
  const int t=invoke<int>(GetPropTexture,ped,row.slot), tn=invoke<int>(NumPropTextures,ped,row.slot,d);
  return "< "+std::to_string(d+1)+"/"+std::to_string(n)+" >"+(tn>1?"  col "+std::to_string(t+1)+"/"+std::to_string(tn):"");
 }
 return {};
}

// Left/Right: next/previous outfit part (skipping combinations the model does not have); Enter: next colour.
void changeRow(int ped,const Row& row,int delta,bool colour) {
 if(row.kind==RowKind::Comp) {
  const int n=invoke<int>(NumDrawables,ped,row.slot); if(n<=0) return;
  int d=invoke<int>(GetDrawable,ped,row.slot), t=invoke<int>(GetTexture,ped,row.slot);
  const int palette=invoke<int>(GetPalette,ped,row.slot);
  if(colour) {
   const int tn=invoke<int>(NumTextures,ped,row.slot,d); if(tn<=1) return;
   for(int step=0;step<tn;++step) { t=(t+1)%tn; if(invoke<int>(ValidComponent,ped,row.slot,d,t)) {invoke(SetComponent,ped,row.slot,d,t,palette);return;} }
   return;
  }
  for(int step=0;step<n;++step) {
   d=((d+delta)%n+n)%n;
   if(invoke<int>(ValidComponent,ped,row.slot,d,0)) {invoke(SetComponent,ped,row.slot,d,0,palette);return;}
  }
 } else if(row.kind==RowKind::Prop) {
  const int n=invoke<int>(NumPropDrawables,ped,row.slot); if(n<=0) return;
  int d=invoke<int>(GetProp,ped,row.slot);
  if(colour) {
   if(d<0) return;
   const int tn=invoke<int>(NumPropTextures,ped,row.slot,d); if(tn<=1) return;
   invoke(SetProp,ped,row.slot,d,(invoke<int>(GetPropTexture,ped,row.slot)+1)%tn,true);
   return;
  }
  d+=delta; if(d<-1) d=n-1; if(d>=n) d=-1;     // -1 = nothing worn
  if(d<0) invoke(ClearProp,ped,row.slot); else invoke(SetProp,ped,row.slot,d,0,true);
 }
}

gmm::Look captureLook(int ped,const std::vector<Row>& rows) {
 gmm::Look look;
 for(auto& row:rows) {
  if(row.kind==RowKind::Comp) {look.comp[row.slot]={invoke<int>(GetDrawable,ped,row.slot),invoke<int>(GetTexture,ped,row.slot)};look.hasComp[row.slot]=true;}
  else if(row.kind==RowKind::Prop) {
   const int d=invoke<int>(GetProp,ped,row.slot);
   look.prop[row.slot]={d,d<0?0:invoke<int>(GetPropTexture,ped,row.slot)};look.hasProp[row.slot]=true;
  }
 }
 return look;
}

void applyLook(int ped,const gmm::Look& look) {
 for(int c=0;c<12;++c)
  if(look.hasComp[c]&&look.comp[c][0]<invoke<int>(NumDrawables,ped,c)&&invoke<int>(ValidComponent,ped,c,look.comp[c][0],look.comp[c][1]))
   invoke(SetComponent,ped,c,look.comp[c][0],look.comp[c][1],invoke<int>(GetPalette,ped,c));
 for(int p=0;p<8;++p) {
  if(!look.hasProp[p]) continue;
  if(look.prop[p][0]<0) invoke(ClearProp,ped,p);
  else if(look.prop[p][0]<invoke<int>(NumPropDrawables,ped,p)) invoke(SetProp,ped,p,look.prop[p][0],look.prop[p][1],true);
 }
}

// Spawn name of the current player model if it is in the catalog (for a readable saved-looks file).
std::string modelName(const std::vector<gmm::Model>& models,std::uint32_t hash) {
 for(auto& m:models) if(hashModel(m.spawn)==hash) return m.spawn;
 return "ped";
}

bool writeLooks(const std::string& text) {
 try {
  std::ofstream file(looksPath(),std::ios::binary|std::ios::trunc);
  if(!file) return false;
  file<<text; return static_cast<bool>(file);
 } catch(...) { return false; }
}

struct Layout { const std::vector<gmm::Model>* models=nullptr; float w=MinW; std::vector<std::string> names; };
Layout layout;
const std::string CustomizeRow="Customize current ped  >";
const Layout& measure(const std::vector<gmm::Model>& models) {
 if(layout.models==&models&&layout.names.size()==models.size()) return layout;
 layout={&models,MinW,{}};
 float widest=width("PED SELECTOR   000/000",TitleScale,TitleFont);
 widest=std::max(widest,width("Up/Down  Enter spawn  Back close  F12 hide",HintScale,RowFont));
 widest=std::max(widest,width(CustomizeRow,RowScale,RowFont));
 for(auto& m:models) widest=std::max(widest,width(m.display,RowScale,RowFont));
 layout.w=std::clamp(widest+PadX*2+BarW*2,MinW,MaxW);
 for(auto& m:models) layout.names.push_back(fit(m.display,RowScale,RowFont,layout.w-PadX*2-BarW*2));
 return layout;
}

// Glossy two-line header flush with the screen corner: big title, small "MOD HUB ADD-ON" line, counter on the right.
void header(float W,const std::string& title,const std::string& counter,const std::string& subtitle="MOD HUB ADD-ON") {
 rect(0,0,W,HeaderH,255,153,0);
 rect(0,0,W,HeaderH*0.42f,255,190,95,120);          // gloss
 rect(0,HeaderH-0.006f,W,0.006f,232,128,0);          // lower lip
 rect(0,HeaderH,W,AccentH,150,80,0);
 const float titleBand=HeaderH*0.62f;
 text(PadX,0,titleBand,title,TitleScale,TitleFont,18,12,2);
 text(PadX,titleBand-0.004f,HeaderH-titleBand,subtitle,HintScale,RowFont,70,38,0);
 if(!counter.empty()) text(0,0,titleBand,counter,RowScale,RowFont,18,12,2,W,true);
}

void footer(float W,float y,const std::string& hint,bool idle) {
 rect(0,y,W,FooterH,12,13,15,245);
 rect(0,y,W,0.0015f,58,61,67);
 text(PadX,y,FooterH,fit(hint,HintScale,RowFont,W-PadX*2),HintScale,RowFont,idle?167:255,idle?171:179,idle?178:71);
}

void row(float W,float y,size_t index,bool active,const std::string& label,const std::string& value,bool accent=false) {
 if(active) {rect(0,y,W,RowH,255,153,0);rect(0,y,BarW,RowH,255,214,150);}
 else rect(0,y,W,RowH,(index&1)?26:22,(index&1)?28:24,(index&1)?33:28,235);
 const int r=active?18:accent?255:240, g=active?12:accent?179:240, b=active?2:accent?71:240;
 text(PadX+BarW,y,RowH,label,RowScale,RowFont,r,g,b);
 if(!value.empty()) text(0,y,RowH,value,RowScale,RowFont,r,g,b,W,true);
}

void scrollbar(float W,size_t total,size_t first) {
 if(total<=VisibleRows) return;
 const float top=HeaderH+AccentH,track=RowH*VisibleRows,thumb=track*VisibleRows/total;
 rect(W-BarW,top,BarW,track,60,62,68,200);
 rect(W-BarW,top+(track-thumb)*first/(total-VisibleRows),BarW,thumb,255,153,0);
}

// Ped list: row 0 opens customization of the current ped, then the installed add-on peds.
void draw(const std::vector<gmm::Model>& models,size_t selected,const std::string& status,unsigned toggle) {
 invoke(ResetAlign);   // another script may leave a safe-zone alignment set: we want the exact corner
 const auto& L=measure(models);const float W=L.w;
 const size_t total=models.size()+1;
 header(W,"PED SELECTOR",models.empty()?"":std::to_string(selected==0?0:selected)+"/"+std::to_string(models.size()));
 float y=HeaderH+AccentH;
 size_t first=selected>=VisibleRows?selected-VisibleRows+1:0;
 for(size_t n=first;n<total&&n<first+VisibleRows;++n,y+=RowH)
  row(W,y,n,n==selected,n==0?CustomizeRow:L.names[n-1],"",n==0);
 if(models.empty()) {
  // Two short lines instead of one long one: the hint must not widen the menu or run past its edge.
  rect(0,y,W,RowH*2,22,24,28,235);
  text(PadX,y,RowH,fit("No add-on peds installed.",RowScale,RowFont,W-PadX*2),RowScale,RowFont,167,171,178);
  text(PadX,y+RowH,RowH,fit("Install them in ModHub.",RowScale,RowFont,W-PadX*2),RowScale,RowFont,167,171,178);
  y+=RowH*2;
 }
 scrollbar(W,total,first);
 const bool idle=status.empty()||status=="Select a ped"||status=="Catalog missing or empty";
 footer(W,y,idle?"Up/Down  Enter spawn  Back close  "+gmm::keyName(toggle)+" hide":status,idle);
}

void drawCustom(int ped,const std::vector<Row>& rows,size_t selected,const std::string& status,const std::string& model) {
 invoke(ResetAlign);
 const float W=std::clamp(std::max(layout.w,0.30f),MinW,MaxW);
 header(W,"CUSTOMIZE",model=="ped"?"":model,"PED SELECTOR  /  MOD HUB ADD-ON");
 float y=HeaderH+AccentH;
 size_t first=selected>=VisibleRows?selected-VisibleRows+1:0;
 for(size_t n=first;n<rows.size()&&n<first+VisibleRows;++n,y+=RowH) {
  const bool action=rows[n].kind==RowKind::Save||rows[n].kind==RowKind::Reset||rows[n].kind==RowKind::Back;
  row(W,y,n,n==selected,rowLabel(rows[n]),rowValue(ped,rows[n]),action);
 }
 scrollbar(W,rows.size(),first);
 const bool idle=status.empty();
 footer(W,y,idle?"Left/Right or 4/6 change  Enter colour  Up/Down select":status,idle);
}
// Game actions bound to the menu's own keys (arrows, Enter, Backspace, NumPad). Everything else stays live while
// the menu is open, so the player can still walk, sprint, jump, look around and drive (WASD, Shift, Space, mouse).
constexpr int MenuActions[]={
 27,                          // phone (arrow up)
 18,176,177,                  // Enter / phone select / phone cancel
 172,173,174,175,             // phone navigation (arrows)
 187,188,189,190,191,194,199,200,201,202, // frontend navigation, accept, cancel, pause
 107,108,109,110,111,112,117,118,  // aircraft controls on NumPad 4/5/6/8/7/9
 123,124,125,126,127,128,     // submarine controls on the NumPad
};
void block(const gmm::Frame& frame) {
 for(int group=0;group<3;++group) {
  if(frame.blockAll) for(int action:MenuActions) invoke(Disable,group,action,false);
  else if(frame.closeHeld) {
   // Standard phone cancel, frontend cancel, pause and script-pad cancellation.
   for(int action:{177,194,199,200,202}) invoke(Disable,group,action,true);
  }
 }
}
void run() {
 auto root=gameRoot(); auto models=gmm::catalog(read(root/L"GMMPedSelector.models.txt"));
 auto toggle=gmm::menuKey(read(root/L"GMMPedSelector.ini"));
 if(!toggle)log(root,"Menu toggle is disabled: None/Disabled or an invalid/unsupported/reserved key binding");
 log(root,"Native Legacy selector started; catalog entries="+std::to_string(models.size())+"; runtime compatibility not validated");
 gmm::Input input; size_t selected=0; std::uint32_t pending=0; ULONGLONG requested=0;
 std::string status=models.empty()?"Catalog missing or empty":"Select a ped";
 // Customization page: rows of the current ped, rebuilt when it opens or after a model change.
 bool custom=false; size_t customSelected=0; std::vector<Row> rows; std::string customStatus;
 for(;;) {
  scriptWait(0);
  gmm::Keys keys{};
  // Read high bits only: global GetAsyncKeyState low-bit edges are unreliable.
  for(unsigned key=0;key<256;++key) keys[key]=(GetAsyncKeyState(key)&0x8000)!=0;
  const bool focus=focused(), safe=playable();
  auto frame=input.update(keys,focus&&safe,toggle);
  if(focus&&safe) block(frame);
  if(pending && (!frame.open||!focus||!safe)) {
   invoke(Release,pending); pending=0; status="Model request cancelled";
  }
  if(!frame.open) { custom=false; continue; }   // reopening always starts at the ped list
  const int ped=invoke<int>(PlayerPedId);

  if(custom) {
   if(!ped) { custom=false; continue; }
   if(frame.move) {customSelected=(customSelected+rows.size()+frame.move)%rows.size();customStatus.clear();}
   const Row& current=rows[customSelected];
   if(frame.change&&(current.kind==RowKind::Comp||current.kind==RowKind::Prop)) {changeRow(ped,current,frame.change,false);customStatus.clear();}
   if(frame.apply) {
    const auto hash=static_cast<std::uint32_t>(invoke<int>(EntityModel,ped));
    const auto name=modelName(models,hash);
    if(current.kind==RowKind::Comp||current.kind==RowKind::Prop) {changeRow(ped,current,0,true);customStatus.clear();}
    else if(current.kind==RowKind::Save) {
     const auto look=captureLook(ped,rows);
     customStatus=writeLooks(gmm::upsertLook(read(looksPath()),hash,name,&look))
      ?"Look saved: applied on every spawn of this ped"
      :"Cannot save: reinstall the menu in ModHub";
     log(root,customStatus+": "+name);
    } else if(current.kind==RowKind::Reset) {
     invoke(Defaults,ped); invoke(ClearAllProps,ped);
     auto text=read(looksPath()); gmm::Look old;
     const bool had=gmm::findLook(text,hash,old);
     customStatus=!had||writeLooks(gmm::upsertLook(text,hash,name,nullptr))?"Default look restored":"Restored, but saved look could not be removed";
     rows=customRows(ped); if(customSelected>=rows.size()) customSelected=rows.size()-1;
    } else { custom=false; status="Select a ped"; }
   }
   if(custom) {drawCustom(ped,rows,customSelected,customStatus,modelName(models,static_cast<std::uint32_t>(invoke<int>(EntityModel,ped))));continue;}
  }

  const size_t total=models.size()+1;   // row 0 = customize the current ped
  if(frame.move) selected=frame.move<0?(selected+total-1)%total:(selected+1)%total;
  if(frame.apply&&selected==0&&!pending) {
   if(ped) {rows=customRows(ped);customSelected=0;customStatus.clear();custom=true;drawCustom(ped,rows,0,"",modelName(models,static_cast<std::uint32_t>(invoke<int>(EntityModel,ped))));continue;}
  } else if(frame.apply&&!pending) {
   auto& entry=models[selected-1];
   auto model=hashModel(entry.spawn);
   if(!invoke<int>(CdImage,model)||!invoke<int>(Valid,model)||!invoke<int>(PedModel,model)) {
    status="Unavailable or invalid ped model";log(root,status+": "+entry.spawn);
   } else {invoke(Request,model);pending=model;requested=GetTickCount64();status="Loading model...";}
  }
  if(pending) {
   if(GetTickCount64()-requested>=5000) {invoke(Release,pending);pending=0;status="Model load timed out";log(root,status);}
   else if(invoke<int>(Loaded,pending)) {
    // Recheck immediately before mutation; SET_PLAYER_MODEL invalidates the old ped.
    if(focused()&&playable()) {
     invoke(Change,invoke<int>(PlayerId),pending);
     auto spawned=invoke<int>(PlayerPedId);
     if(spawned) {
      invoke(Defaults,spawned);
      gmm::Look look;   // the player's saved outfit for this ped, if any
      if(gmm::findLook(read(looksPath()),pending,look)) {applyLook(spawned,look);status="Model applied with saved look";}
      else status="Model applied";
     }
     log(root,status);
    } else status="Model change cancelled";
    invoke(Release,pending);pending=0;
   }
  }
  draw(models,selected,status,toggle);
 }
}
void scriptMain() {
 try {run();} catch(const std::exception& error) {try{log(gameRoot(),std::string("Selector stopped: ")+error.what());}catch(...) {}}
 catch(...) {OutputDebugStringA("GMMPedSelector stopped after an unexpected C++ error\n");}
 // Per-frame controls reset naturally; do not keep blocking after an error.
 for(;;) scriptWait(1000);
}
bool registered=false;
}
BOOL APIENTRY DllMain(HMODULE module,DWORD reason,LPVOID) {
 if(reason==DLL_PROCESS_ATTACH) {
  auto exe=modulePath(nullptr);
  if(exe.empty()) return TRUE;
  const auto slash=exe.find_last_of(L'\\');
  if(_wcsicmp(slash==std::wstring::npos?exe.c_str():exe.c_str()+slash+1,L"GTA5.exe")!=0) return TRUE;
  scriptRegister(module,scriptMain);registered=true;
 } else if(reason==DLL_PROCESS_DETACH&&registered) scriptUnregister(module);
 return TRUE;
}
