#import <AppKit/AppKit.h>
#import <ApplicationServices/ApplicationServices.h>
#import <Carbon/Carbon.h>
#import <GameController/GameController.h>
#import <IOKit/hidsystem/ev_keymap.h>
#import <QuartzCore/QuartzCore.h>
#include <stdint.h>
#include <stdlib.h>
#include <unistd.h>

typedef struct { float x, y, width, height; } DMRect;
typedef struct { uint32_t id; int32_t owner; DMRect frame; } DMWindowInfo;
typedef struct { uint32_t id; DMRect frame, workArea; } DMDisplayInfo;
typedef struct { int32_t connected; uint32_t buttons; float leftX, leftY, l2, r2; } DMGamepadState;
typedef struct { const void *pixels; int32_t stride, width, height; DMRect bounds; } DMImageLayer;
typedef struct { DMImageLayer border, artwork, tail; float opacity; } DMPortraitLayer;
typedef struct { DMImageLayer image; int32_t depth, itemId; DMRect hitBounds; int32_t hitMode; float opacity; } DMItemLayer;

typedef int32_t (*DMStart)(void);
typedef void (*DMFrame)(double);
typedef void (*DMKey)(int32_t, int32_t);
typedef void (*DMMouse)(int32_t, float, float, double, int32_t, int32_t);
typedef void (*DMCommand)(int32_t, int32_t);
typedef void (*DMStop)(void);

enum { DMCapture = 1, DMReset, DMAutonomy, DMSound, DMScale, DMTerrain, DMChooseGame, DMHelp, DMQuit, DMRumble,
    DMExpressions, DMDesktopItems, DMClearItems, DMKeyBindings = 17, DMBadelineCount,
    DMInfiniteStamina, DMDashAssist };
enum { DMMouseDown, DMMouseDrag, DMMouseUp, DMMouseCancel, DMPlaceItem, DMDeleteItem };
enum { DMHitAlpha, DMHitRegion, DMHitResize };
enum { DMItemCount = 35 };
enum {
    DMUp = 1 << 0, DMDown = 1 << 1, DMLeft = 1 << 2, DMRight = 1 << 3,
    DMCross = 1 << 4, DMCircle = 1 << 5, DMSquare = 1 << 6, DMTriangle = 1 << 7,
    DML1 = 1 << 8, DMR1 = 1 << 9,
};

static CGFloat primaryTop(void) {
    return NSMaxY(NSScreen.screens.firstObject.frame);
}

static DMRect quartzRect(NSRect rect, CGFloat top) {
    return (DMRect){NSMinX(rect), top - NSMaxY(rect), NSWidth(rect), NSHeight(rect)};
}

static NSNumber *displayId(NSScreen *screen) {
    return screen.deviceDescription[@"NSScreenNumber"];
}

@interface DMPixelLayer : CALayer
@property(nonatomic, strong) NSData *imagePixels;
@property(nonatomic) int pixelWidth, pixelHeight;
@end

@implementation DMPixelLayer
@end

@interface DMOverlay : NSPanel
@property(nonatomic, strong) DMPixelLayer *sprite;
@property(nonatomic, strong) CALayer *portraitGroup;
@property(nonatomic, strong) DMPixelLayer *portraitBorder;
@property(nonatomic, strong) DMPixelLayer *portraitArtwork;
@property(nonatomic, strong) DMPixelLayer *portraitTail;
@property(nonatomic, strong) NSMutableArray<DMPixelLayer *> *items;
@end

@implementation DMOverlay
- (BOOL)canBecomeKeyWindow { return YES; }
- (BOOL)canBecomeMainWindow { return NO; }
@end

static DMPixelLayer *addImageLayer(CALayer *parent, NSString *filter) {
    DMPixelLayer *layer = [DMPixelLayer layer];
    layer.contentsGravity = kCAGravityResize;
    layer.magnificationFilter = filter;
    layer.minificationFilter = filter;
    [parent addSublayer:layer];
    return layer;
}

@class DMHost;

@interface DMDragView : NSView
@property(nonatomic, weak) DMHost *host;
@end

@interface DMItemTile : NSView
@property(nonatomic, weak) DMHost *host;
@property(nonatomic) NSPoint downPoint;
@property(nonatomic) BOOL dragging;
@property(nonatomic) int kind;
@end

@interface DMItemPaletteView : NSView
@end

@implementation DMItemPaletteView
- (void)drawRect:(NSRect)dirtyRect {
    NSRectFillUsingOperation(dirtyRect, NSCompositingOperationClear);
    NSBezierPath *shape = [NSBezierPath bezierPathWithRoundedRect:NSInsetRect(self.bounds, .5, .5) xRadius:13 yRadius:13];
    [NSColor.windowBackgroundColor setFill];
    [shape fill];
    [[NSColor.separatorColor colorWithAlphaComponent:.35] setStroke];
    shape.lineWidth = 1;
    [shape stroke];
}
@end

@interface DMHost : NSObject <NSApplicationDelegate, NSMenuDelegate, NSWindowDelegate>
@property(nonatomic) DMStart start;
@property(nonatomic) DMFrame frame;
@property(nonatomic) DMKey key;
@property(nonatomic) DMMouse mouse;
@property(nonatomic) DMCommand command;
@property(nonatomic) DMStop stop;
@property(nonatomic, strong) NSStatusItem *status;
@property(nonatomic, strong) NSMenu *menu;
@property(nonatomic, strong) NSMutableDictionary<NSNumber *, DMOverlay *> *overlays;
@property(nonatomic, strong) NSPanel *dragPanel;
@property(nonatomic, strong) CALayer *dragImage;
@property(nonatomic, strong) NSMutableDictionary<NSNumber *, id> *itemIcons;
@property(nonatomic, strong) NSPanel *itemPalette;
@property(nonatomic, strong) NSPanel *itemPreview;
@property(nonatomic, strong) id hoverImage;
@property(nonatomic) NSRect hoverFrame;
@property(nonatomic) NSRect hoverEditFrame;
@property(nonatomic) int hoverHitMode, hitResize, dragResize;
@property(nonatomic) int hoverItem, hoverDepth, hitItem, dragItem;
@property(nonatomic) BOOL hasDesktopItems;
@property(nonatomic) int courseState;
@property(nonatomic, strong) NSPanel *courseNotice;
@property(nonatomic, strong) NSTimer *timer;
@property(nonatomic, strong) CADisplayLink *displayLink;
@property(nonatomic, strong) NSData *windowSnapshot;
@property(nonatomic) uint64_t windowSnapshotGeneration;
@property(nonatomic) BOOL windowSampleInFlight, trackingWindowDrag;
@property(nonatomic) int snapshotFrontmost;
@property(nonatomic, strong) id keyMonitor;
@property(nonatomic) CFTimeInterval last;
@property(nonatomic) BOOL capturing, dragging;
@property(nonatomic) BOOL choosing;
@property(nonatomic) BOOL chinese;
@property(nonatomic) int scale, autonomy, sound, audioAvailable, terrain, rumble;
@property(nonatomic) BOOL showExpressions;
@property(nonatomic) int badelineCount, badelineLimit, dashAssist;
@property(nonatomic) BOOL infiniteStamina;
@property(nonatomic) uint32_t hiddenDisplay;
@property(nonatomic, copy) NSArray<NSNumber *> *keyboardCodes;
@property(nonatomic, copy) NSArray<NSString *> *keyboardLabels;
@property(nonatomic, strong) NSArray<NSButton *> *bindingButtons;
@property(nonatomic, weak) NSTextField *bindingHint;
@property(nonatomic) NSInteger editingBinding;
- (void)buildMenu;
- (void)syncDisplays;
- (void)startFrameClock;
- (void)captureKeyboard;
- (void)releaseKeyboard;
- (void)dragEvent:(NSEvent *)event action:(int)action;
- (void)deleteItemEvent:(NSEvent *)event;
- (void)closeItemPalette;
- (void)showKeyBindings;
- (BOOL)canDropAt:(NSPoint)point;
- (void)previewItem:(int)kind at:(NSPoint)point;
- (void)dropItem:(int)kind at:(NSPoint)point timestamp:(double)timestamp;
@end

static DMHost *host;

static const CGFloat resizeBand = 6;

static int resizeAt(NSPoint point, NSRect rect) {
    if (!NSPointInRect(point, NSInsetRect(rect, -resizeBand, -resizeBand))) return 0;
    int edges = 0;
    if (fabs(point.x - NSMinX(rect)) <= resizeBand) edges |= NSCursorFrameResizePositionLeft;
    else if (fabs(point.x - NSMaxX(rect)) <= resizeBand) edges |= NSCursorFrameResizePositionRight;
    if (fabs(point.y - NSMaxY(rect)) <= resizeBand) edges |= NSCursorFrameResizePositionTop;
    else if (fabs(point.y - NSMinY(rect)) <= resizeBand) edges |= NSCursorFrameResizePositionBottom;
    return edges;
}

static void setResizeCursor(int edges) {
    if (edges == host.hitResize) return;
    host.hitResize = edges;
    NSCursor *cursor = edges
        ? [NSCursor frameResizeCursorFromPosition:(NSCursorFrameResizePosition)edges inDirections:NSCursorFrameResizeDirectionsAll]
        : NSCursor.arrowCursor;
    [cursor set];
}

@implementation DMDragView
- (BOOL)acceptsFirstMouse:(NSEvent *)event { return YES; }
- (BOOL)mouseDownCanMoveWindow { return NO; }
- (void)mouseDown:(NSEvent *)event { [self.host dragEvent:event action:DMMouseDown]; }
- (void)mouseDragged:(NSEvent *)event { [self.host dragEvent:event action:DMMouseDrag]; }
- (void)mouseUp:(NSEvent *)event { [self.host dragEvent:event action:DMMouseUp]; }
- (void)rightMouseDown:(NSEvent *)event { [self.host deleteItemEvent:event]; }
@end

@implementation DMItemTile
- (BOOL)acceptsFirstMouse:(NSEvent *)event { return YES; }
- (BOOL)mouseDownCanMoveWindow { return NO; }
- (void)resetCursorRects { [self addCursorRect:self.bounds cursor:NSCursor.openHandCursor]; }
// Route the whole tile, including its labels, to the same drag source.
- (NSView *)hitTest:(NSPoint)point { return [super hitTest:point] ? self : nil; }
- (void)mouseDown:(NSEvent *)event {
    self.downPoint = [self.window convertPointToScreen:event.locationInWindow];
    self.dragging = NO;
}
- (void)mouseDragged:(NSEvent *)event {
    NSPoint point = [self.window convertPointToScreen:event.locationInWindow];
    if (!self.dragging && hypot(point.x - self.downPoint.x, point.y - self.downPoint.y) < 4) return;
    self.dragging = YES;
    [self.host previewItem:self.kind at:point];
}
- (void)mouseUp:(NSEvent *)event {
    if (!self.dragging) return;
    self.dragging = NO;
    [self.host dropItem:self.kind at:[self.window convertPointToScreen:event.locationInWindow] timestamp:event.timestamp];
}
@end

@implementation DMHost

static NSArray<NSNumber *> *defaultKeyboardCodes(void) {
    return @[@(kVK_ANSI_A), @(kVK_ANSI_D), @(kVK_ANSI_W), @(kVK_ANSI_S),
        @(kVK_ANSI_C), @(kVK_ANSI_X), @(kVK_ANSI_Z)];
}

static NSArray<NSString *> *defaultKeyboardLabels(void) {
    return @[@"A", @"D", @"W", @"S", @"C", @"X", @"Z"];
}

static BOOL secondaryKeyboardKey(unsigned short code) {
    return code == kVK_LeftArrow || code == kVK_RightArrow || code == kVK_UpArrow ||
        code == kVK_DownArrow || code == kVK_ANSI_V || code == kVK_Shift;
}

static BOOL validKeyboardKey(unsigned short code) {
    return code < 128 && code != kVK_Escape && !secondaryKeyboardKey(code);
}

- (void)loadKeyBindings {
    NSDictionary *saved = [NSUserDefaults.standardUserDefaults dictionaryForKey:@"KeyboardBindings"];
    NSArray *codes = saved[@"codes"], *labels = saved[@"labels"];
    self.keyboardCodes = defaultKeyboardCodes();
    self.keyboardLabels = defaultKeyboardLabels();
    self.editingBinding = -1;
    if (![codes isKindOfClass:NSArray.class] || ![labels isKindOfClass:NSArray.class] ||
        codes.count != self.keyboardCodes.count || labels.count != codes.count ||
        [NSSet setWithArray:codes].count != codes.count) return;
    for (NSUInteger i = 0; i < codes.count; i++) {
        if (![codes[i] isKindOfClass:NSNumber.class] || !validKeyboardKey([codes[i] unsignedShortValue]) ||
            ![labels[i] isKindOfClass:NSString.class] || [labels[i] length] == 0) return;
    }
    self.keyboardCodes = codes;
    self.keyboardLabels = labels;
}

- (void)refreshBindingButtons {
    for (NSUInteger i = 0; i < self.bindingButtons.count; i++) {
        self.bindingButtons[i].title = (NSInteger)i == self.editingBinding
            ? [self label:@"按键…" english:@"Press a key…"] : self.keyboardLabels[i];
    }
}

- (void)selectBinding:(NSButton *)sender {
    self.editingBinding = sender.tag;
    [self refreshBindingButtons];
    self.bindingHint.stringValue = [self label:@"按下新按键，或用 Esc 取消。"
        english:@"Press a new key, or Esc to cancel."];
}

- (void)resetKeyBindings:(__unused NSButton *)sender {
    self.keyboardCodes = defaultKeyboardCodes();
    self.keyboardLabels = defaultKeyboardLabels();
    self.editingBinding = -1;
    [self refreshBindingButtons];
    self.bindingHint.stringValue = [self label:@"已恢复默认按键。" english:@"Default keys restored."];
}

- (BOOL)recordBinding:(NSEvent *)event {
    if (self.editingBinding < 0 || event.type != NSEventTypeKeyDown) return NO;
    if (event.isARepeat) return YES;
    unsigned short code = event.keyCode;
    if (code != kVK_Escape) {
        NSUInteger existing = [self.keyboardCodes indexOfObject:@(code)];
        if (!validKeyboardKey(code) ||
            (event.modifierFlags & (NSEventModifierFlagCommand | NSEventModifierFlagControl | NSEventModifierFlagOption)) ||
            (existing != NSNotFound && existing != (NSUInteger)self.editingBinding)) {
            self.bindingHint.stringValue = [self label:@"此键已被占用或保留，请换一个按键。"
                english:@"That key is assigned or reserved. Choose another."];
            return YES;
        }
        NSMutableArray *codes = self.keyboardCodes.mutableCopy;
        NSMutableArray *labels = self.keyboardLabels.mutableCopy;
        NSString *label = [event.charactersIgnoringModifiers stringByTrimmingCharactersInSet:
            NSCharacterSet.whitespaceAndNewlineCharacterSet].uppercaseString;
        if (code == kVK_Space) label = [self label:@"空格" english:@"Space"];
        if (code == kVK_Return) label = [self label:@"回车" english:@"Return"];
        if (code == kVK_Tab) label = @"Tab";
        if (label.length == 0 || [label characterAtIndex:0] < 32 || [label characterAtIndex:0] >= 0xf700)
            label = [NSString stringWithFormat:@"Key %u", code];
        codes[self.editingBinding] = @(code);
        labels[self.editingBinding] = label;
        self.keyboardCodes = codes;
        self.keyboardLabels = labels;
    }
    self.editingBinding = -1;
    [self refreshBindingButtons];
    self.bindingHint.stringValue = [self label:@"选择要修改的动作。" english:@"Choose an action to change."];
    return YES;
}

- (void)showKeyBindings {
    BOOL restoreCapture = self.capturing;
    [self releaseKeyboard];
    [self closeItemPalette];
    [NSApp activate];
    self.choosing = YES;
    NSAlert *dialog = [[NSAlert alloc] init];
    dialog.messageText = [self label:@"自定义按键" english:@"Customize Keys"];
    [dialog addButtonWithTitle:[self label:@"完成" english:@"Done"]];
    NSView *form = [[NSView alloc] initWithFrame:NSMakeRect(0, 0, 330, 310)];
    NSArray *actions = self.chinese ? @[@"向左", @"向右", @"向上", @"向下 / 下蹲", @"跳跃", @"冲刺", @"抓墙"]
                                      : @[@"Left", @"Right", @"Up", @"Down / Crouch", @"Jump", @"Dash", @"Grab"];
    NSMutableArray *buttons = [NSMutableArray array];
    for (NSUInteger i = 0; i < actions.count; i++) {
        CGFloat y = 277 - 33 * i;
        NSTextField *name = [NSTextField labelWithString:actions[i]];
        name.frame = NSMakeRect(0, y + 3, 184, 20);
        [form addSubview:name];
        NSButton *button = [NSButton buttonWithTitle:@"" target:self action:@selector(selectBinding:)];
        button.tag = (NSInteger)i;
        button.frame = NSMakeRect(214, y, 115, 27);
        [form addSubview:button];
        [buttons addObject:button];
    }
    NSTextField *hint = [NSTextField labelWithString:[self label:@"选择要修改的动作。"
        english:@"Choose an action to change."]];
    hint.frame = NSMakeRect(0, 39, 330, 19);
    hint.textColor = NSColor.secondaryLabelColor;
    hint.font = [NSFont systemFontOfSize:11];
    [form addSubview:hint];
    NSButton *reset = [NSButton buttonWithTitle:[self label:@"恢复默认" english:@"Restore Defaults"]
        target:self action:@selector(resetKeyBindings:)];
    reset.frame = NSMakeRect(0, 5, 129, 27);
    [form addSubview:reset];
    self.bindingHint = hint;
    self.bindingButtons = buttons;
    [self refreshBindingButtons];
    dialog.accessoryView = form;
    [dialog runModal];
    self.editingBinding = -1;
    self.bindingButtons = nil;
    [NSUserDefaults.standardUserDefaults setObject:@{@"codes":self.keyboardCodes, @"labels":self.keyboardLabels}
        forKey:@"KeyboardBindings"];
    self.last = 0;
    self.choosing = NO;
    if (restoreCapture) [self captureKeyboard];
}

- (NSString *)label:(NSString *)chinese english:(NSString *)english {
    return self.chinese ? chinese : english;
}

- (NSMenuItem *)item:(NSString *)title in:(NSMenu *)menu command:(int)command argument:(int)argument {
    NSMenuItem *item = [[NSMenuItem alloc] initWithTitle:title action:@selector(menuAction:) keyEquivalent:@""];
    item.target = self;
    item.tag = command;
    item.representedObject = @(argument);
    [menu addItem:item];
    return item;
}

- (void)buildMenu {
    [self.menu removeAllItems];
    [self item:self.capturing ? [self label:@"释放键盘（Esc）" english:@"Release Keyboard (Esc)"]
                              : [self label:@"控制 Madeline" english:@"Control Madeline"]
             in:self.menu command:DMCapture argument:0];
    [self item:[self label:@"回到桌面上方" english:@"Reset Position"] in:self.menu command:DMReset argument:0];
    [self.menu addItem:NSMenuItem.separatorItem];

    NSMenuItem *items = [self item:[self label:@"桌面道具…" english:@"Desktop Items…"] in:self.menu command:DMDesktopItems argument:0];
    if (self.itemIcons.count == 0) { items.action = nil; items.enabled = NO; }
    NSMenuItem *clear = [self item:[self label:@"清空桌面道具" english:@"Clear Desktop Items"] in:self.menu command:DMClearItems argument:0];
    if (!self.hasDesktopItems) { clear.action = nil; clear.enabled = NO; }
    [self.menu addItem:NSMenuItem.separatorItem];

    NSMenuItem *autonomy = [self item:[self label:@"自动探索" english:@"Explore Automatically"] in:self.menu command:DMAutonomy argument:0];
    autonomy.state = self.autonomy ? NSControlStateValueOn : NSControlStateValueOff;
    NSMenuItem *expressions = [self item:[self label:@"显示表情" english:@"Show Expressions"] in:self.menu command:DMExpressions argument:0];
    expressions.state = self.showExpressions ? NSControlStateValueOn : NSControlStateValueOff;
    NSMenuItem *chase = [[NSMenuItem alloc] initWithTitle:[self label:@"Badeline 追逐" english:@"Badeline Chase"]
        action:nil keyEquivalent:@""];
    chase.submenu = [[NSMenu alloc] initWithTitle:chase.title];
    for (int i = 0; i <= self.badelineLimit; i++) {
        NSString *title = i == 0 ? [self label:@"关闭" english:@"Off"] :
            (self.chinese ? [NSString stringWithFormat:@"%d 个分身", i] : [NSString stringWithFormat:@"%d", i]);
        NSMenuItem *option = [self item:title in:chase.submenu command:DMBadelineCount argument:i];
        option.state = i == self.badelineCount ? NSControlStateValueOn : NSControlStateValueOff;
    }
    [self.menu addItem:chase];

    NSMenuItem *assist = [[NSMenuItem alloc] initWithTitle:[self label:@"辅助" english:@"Assist"]
        action:nil keyEquivalent:@""];
    assist.submenu = [[NSMenu alloc] initWithTitle:assist.title];
    NSMenuItem *stamina = [self item:[self label:@"无限体力" english:@"Infinite Stamina"]
        in:assist.submenu command:DMInfiniteStamina argument:0];
    stamina.state = self.infiniteStamina ? NSControlStateValueOn : NSControlStateValueOff;
    NSMenuItem *dashes = [[NSMenuItem alloc] initWithTitle:[self label:@"冲刺次数" english:@"Dash Count"]
        action:nil keyEquivalent:@""];
    dashes.submenu = [[NSMenu alloc] initWithTitle:dashes.title];
    NSArray<NSString *> *dashLabels = self.chinese ? @[@"一次", @"两次", @"无限次"] :
        @[@"One", @"Two", @"Infinite"];
    const int dashModes[] = {1, 2, -1};
    for (int i = 0; i < 3; i++) {
        NSMenuItem *option = [self item:dashLabels[i] in:dashes.submenu command:DMDashAssist argument:dashModes[i]];
        option.state = dashModes[i] == self.dashAssist ? NSControlStateValueOn : NSControlStateValueOff;
    }
    [assist.submenu addItem:dashes];
    [self.menu addItem:assist];

    NSMenuItem *sound = [self item:[self label:@"原版音效" english:@"Original Sound Effects"] in:self.menu command:DMSound argument:0];
    sound.state = self.sound && self.audioAvailable ? NSControlStateValueOn : NSControlStateValueOff;
    if (!self.audioAvailable) {
        NSMenuItem *unavailable = [[NSMenuItem alloc] initWithTitle:
            [self label:@"原版音效不可用" english:@"Original audio unavailable"] action:nil keyEquivalent:@""];
        [self.menu addItem:unavailable];
    }

    NSMenuItem *rumble = [[NSMenuItem alloc] initWithTitle:[self label:@"手柄震动" english:@"Controller Rumble"] action:nil keyEquivalent:@""];
    rumble.submenu = [[NSMenu alloc] initWithTitle:rumble.title];
    NSArray<NSString *> *rumbleLevels = @[
        [self label:@"关闭" english:@"Off"], [self label:@"减半" english:@"Half"], [self label:@"开启" english:@"On"]
    ];
    for (int i = 0; i < (int)rumbleLevels.count; i++) {
        NSMenuItem *option = [self item:rumbleLevels[i] in:rumble.submenu command:DMRumble argument:i];
        option.state = i == self.rumble ? NSControlStateValueOn : NSControlStateValueOff;
    }
    [self.menu addItem:rumble];

    NSMenuItem *size = [[NSMenuItem alloc] initWithTitle:[self label:@"角色大小" english:@"Size"] action:nil keyEquivalent:@""];
    size.submenu = [[NSMenu alloc] initWithTitle:size.title];
    for (int i = 2; i <= 6; i++) {
        NSMenuItem *option = [self item:[NSString stringWithFormat:@"%d×", i] in:size.submenu command:DMScale argument:i];
        option.state = i == self.scale ? NSControlStateValueOn : NSControlStateValueOff;
    }
    [self.menu addItem:size];

    NSMenuItem *material = [[NSMenuItem alloc] initWithTitle:[self label:@"窗口材质" english:@"Window Material"] action:nil keyEquivalent:@""];
    material.submenu = [[NSMenu alloc] initWithTitle:material.title];
    NSArray<NSString *> *names = @[
        [self label:@"实心边框" english:@"Solid Borders"],
        [self label:@"梦块" english:@"Dream Blocks"],
        [self label:@"水域" english:@"Water"],
    ];
    for (int i = 0; i < (int)names.count; i++) {
        NSMenuItem *option = [self item:names[i] in:material.submenu command:DMTerrain argument:i];
        option.state = i == self.terrain ? NSControlStateValueOn : NSControlStateValueOff;
    }
    [self.menu addItem:material];
    [self.menu addItem:NSMenuItem.separatorItem];
    [self item:[self label:@"自定义按键…" english:@"Customize Keys…"] in:self.menu command:DMKeyBindings argument:0];
    [self item:[self label:@"选择《Celeste》…" english:@"Choose Celeste…"] in:self.menu command:DMChooseGame argument:0];
    [self item:[self label:@"操作说明…" english:@"Controls…"] in:self.menu command:DMHelp argument:0];
    [self item:[self label:@"退出 Desk Madeline" english:@"Quit Desk Madeline"] in:self.menu command:DMQuit argument:0];
}

- (void)menuNeedsUpdate:(NSMenu *)menu { [self buildMenu]; }

- (void)menuAction:(NSMenuItem *)item {
    int command = (int)item.tag;
    int argument = [item.representedObject intValue];
    dispatch_async(dispatch_get_main_queue(), ^{
        if (command == DMCapture) {
            if (self.capturing) [self releaseKeyboard];
            else [self captureKeyboard];
        } else if (command == DMDesktopItems) {
            [self showItemPalette];
        } else if (command == DMKeyBindings) {
            [self showKeyBindings];
        } else if (command == DMHelp) {
            [self releaseKeyboard];
            [NSApp activate];
            NSAlert *alert = [[NSAlert alloc] init];
            alert.messageText = [self label:@"Desk Madeline 操作说明" english:@"Desk Madeline Controls"];
            alert.informativeText = [self
                label:@"启动后即可操控。默认使用 WASD 或方向键移动和瞄准，S／↓ 下蹲，C 跳，X 冲刺，Z／V／左 Shift 抓墙。用左键按住角色可拖动。在菜单栏选择「自定义按键…」可修改主键。\n\nDualSense：左摇杆或十字键移动，×／△ 跳，□／○ 冲刺，L1／R1／L2／R2 抓墙。\n\nEsc 或切换应用可释放键盘。点击角色或在菜单栏选择「控制 Madeline」可恢复操控。"
                english:@"Control is active at launch. By default, WASD or arrows move and aim, S / ↓ crouches, C jumps, X dashes, and Z / V / Left Shift grabs. Hold the left mouse button on Madeline to drag her. Choose Customize Keys from the menu bar to change the primary keys.\n\nDualSense: left stick or D-pad to move, Cross / Triangle to jump, Square / Circle to dash, L1 / R1 / L2 / R2 to grab.\n\nEsc or switching apps releases the keyboard. Click Madeline or choose Control Madeline to take over again."];
            [alert runModal];
        } else if (command == DMQuit) {
            [NSApp terminate:nil];
        } else {
            self.command(command, argument);
        }
    });
}

- (void)showItemPalette {
    if (self.itemIcons.count == 0) return;
    if (self.itemPalette.visible) { [self closeItemPalette]; return; }

    NSStatusBarButton *button = self.status.button;
    NSRect anchor = [button.window convertRectToScreen:[button convertRect:button.bounds toView:nil]];
    NSRect visible = (button.window.screen ?: NSScreen.screens.firstObject).visibleFrame;
    CGFloat width = 338, height = fmin(478, NSHeight(visible) - 18);
    CGFloat x = fmax(NSMinX(visible) + 6, fmin(NSMidX(anchor) - width / 2, NSMaxX(visible) - width - 6));
    CGFloat y = fmax(NSMinY(visible) + 6, fmin(NSMinY(anchor), NSMaxY(visible)) - height - 6);
    NSPanel *panel = [[NSPanel alloc] initWithContentRect:NSMakeRect(x, y, width, height)
        styleMask:NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel
        backing:NSBackingStoreBuffered defer:NO];
    panel.backgroundColor = NSColor.clearColor;
    panel.opaque = NO;
    panel.hasShadow = YES;
    panel.hidesOnDeactivate = NO;
    panel.level = NSPopUpMenuWindowLevel;
    panel.collectionBehavior = NSWindowCollectionBehaviorMoveToActiveSpace | NSWindowCollectionBehaviorIgnoresCycle |
        NSWindowCollectionBehaviorFullScreenAuxiliary;

    NSRect contentBounds = NSMakeRect(0, 0, width, height);
    NSView *root;
    if (@available(macOS 26.0, *)) {
        NSGlassEffectView *glass = [[NSGlassEffectView alloc] initWithFrame:contentBounds];
        glass.style = NSGlassEffectViewStyleRegular;
        glass.cornerRadius = 22;
#if __MAC_OS_X_VERSION_MAX_ALLOWED >= 270000
        if (@available(macOS 27.0, *)) glass.effectIsInteractive = YES;
#endif
        root = [[NSView alloc] initWithFrame:contentBounds];
        root.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        glass.contentView = root;
        panel.contentView = glass;
    } else {
        root = [[DMItemPaletteView alloc] initWithFrame:contentBounds];
        panel.contentView = root;
    }
    NSTextField *hint = [NSTextField labelWithString:[self label:@"拖到桌面 · 右键移除" english:@"Drag to place · Right-click to remove"]];
    hint.font = [NSFont systemFontOfSize:11 weight:NSFontWeightMedium];
    hint.textColor = NSColor.secondaryLabelColor;
    hint.frame = NSMakeRect(17, height - 30, width - 58, 16);
    [root addSubview:hint];

    NSArray<NSString *> *titles = self.chinese
        ? @[@"冲刺回复晶体", @"横长轨道平台", @"煤球", @"蓝云", @"金色羽毛", @"地面弹簧",
            @"方形轨道平台", @"竖长轨道平台", @"绿色泡泡", @"红色泡泡",
            @"左行砖块车", @"右行砖块车", @"上行砖块车", @"下行砖块车",
            @"弹射球", @"墙面弹簧 →", @"墙面弹簧 ←", @"梦境方块", @"粉云", @"双冲刺晶体", @"碎裂平台",
            @"交换方块", @"凯文方块", @"河豚", @"水母", @"草莓", @"触碰开关", @"移动门",
            @"蓝色节拍方块", @"粉色节拍方块", @"黄色节拍方块", @"绿色节拍方块", @"冰火块", @"冰火开关", @"水晶之心"]
        : @[@"Dash Refill", @"Wide Zip Mover", @"Dust Bunny", @"Blue Cloud", @"Golden Feather", @"Ground Spring",
            @"Square Zip Mover", @"Tall Zip Mover", @"Green Booster", @"Red Booster",
            @"Move Block Left", @"Move Block Right", @"Move Block Up", @"Move Block Down",
            @"Bumper", @"Wall Spring →", @"Wall Spring ←", @"Dream Block", @"Pink Cloud", @"Double Refill", @"Crumble Platform",
            @"Swap Block", @"Kevin Block", @"Pufferfish", @"Jellyfish", @"Strawberry", @"Touch Switch", @"Switch Gate",
            @"Blue Cassette", @"Pink Cassette", @"Yellow Cassette", @"Green Cassette", @"Bounce Block", @"Core Switch", @"Crystal Heart"];
    const int order[DMItemCount] = {34, 14, 0, 19, 3, 18, 5, 15, 16, 17, 20, 4, 8, 9, 1, 6, 7, 21,
        10, 11, 12, 13, 22, 2, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33};
    NSScrollView *scroll = [[NSScrollView alloc] initWithFrame:NSMakeRect(0, 10, width, height - 50)];
    scroll.drawsBackground = NO;
    scroll.hasVerticalScroller = YES;
    scroll.scrollerStyle = NSScrollerStyleOverlay;
    CGFloat tileTop = ((DMItemCount + 1) / 2) * 50 + 2;
    NSView *tileParent = [[NSView alloc] initWithFrame:NSMakeRect(0, 0, width, tileTop)];
    scroll.documentView = tileParent;
    [scroll.contentView scrollToPoint:NSMakePoint(0, tileTop - NSHeight(scroll.contentView.bounds))];
    [scroll reflectScrolledClipView:scroll.contentView];
    [root addSubview:scroll];
    CGFloat tileWidth = 154, tileHeight = 44;
    for (int index = 0; index < DMItemCount; index++) {
        int kind = order[index];
        id iconImage = self.itemIcons[@(kind)];
        NSRect frame = NSMakeRect(11 + (index % 2) * (tileWidth + 8),
            tileTop - tileHeight - (index / 2) * (tileHeight + 6), tileWidth, tileHeight);
        DMItemTile *tile = [[DMItemTile alloc] initWithFrame:frame];
        tile.host = iconImage ? self : nil;
        tile.kind = kind;
        tile.toolTip = titles[kind];
        tile.wantsLayer = YES;
        tile.layer.cornerRadius = 8;
        tile.layer.backgroundColor = [NSColor.labelColor colorWithAlphaComponent:.065].CGColor;
        if (iconImage) {
            CALayer *icon = addImageLayer(tile.layer, kCAFilterNearest);
            CGImageRef art = (__bridge CGImageRef)iconImage;
            CGFloat imageWidth = CGImageGetWidth(art), imageHeight = CGImageGetHeight(art);
            CGFloat iconScale = fmin(2, fmin(40 / imageWidth, 32 / imageHeight));
            imageWidth *= iconScale;
            imageHeight *= iconScale;
            icon.frame = CGRectMake(25 - imageWidth / 2, tileHeight / 2 - imageHeight / 2, imageWidth, imageHeight);
            icon.contents = iconImage;
        }
        NSTextField *title = [NSTextField labelWithString:titles[kind]];
        title.font = [NSFont systemFontOfSize:12 weight:NSFontWeightSemibold];
        title.lineBreakMode = NSLineBreakByTruncatingTail;
        title.textColor = iconImage ? NSColor.labelColor : NSColor.disabledControlTextColor;
        title.frame = NSMakeRect(50, 13, 101, 17);
        [tile addSubview:title];
        [tileParent addSubview:tile];
    }

    NSImage *closeIcon = [NSImage imageWithSystemSymbolName:@"xmark.circle.fill"
        accessibilityDescription:[self label:@"关闭" english:@"Close"]];
    NSButton *close = [NSButton buttonWithImage:closeIcon target:self action:@selector(closeItemPaletteButton:)];
    close.frame = NSMakeRect(width - 32, height - 33, 24, 24);
    close.bordered = NO;
    close.contentTintColor = NSColor.secondaryLabelColor;
    [root addSubview:close];

    self.itemPalette = panel;
    [panel orderFrontRegardless];
    [panel displayIfNeeded];
    [panel invalidateShadow];
}

- (void)previewItem:(int)kind at:(NSPoint)point {
    id icon = self.itemIcons[@(kind)];
    if (!self.itemPalette.visible || !icon) return;
    CGImageRef art = (__bridge CGImageRef)icon;
    CGFloat width = CGImageGetWidth(art) * self.scale, height = CGImageGetHeight(art) * self.scale;
    NSRect frame = NSMakeRect(point.x - width / 2, point.y - height / 2, width, height);
    if (!self.itemPreview) {
        NSPanel *panel = [[NSPanel alloc] initWithContentRect:frame
            styleMask:NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel
            backing:NSBackingStoreBuffered defer:NO];
        panel.opaque = NO;
        panel.backgroundColor = NSColor.clearColor;
        panel.hasShadow = NO;
        panel.hidesOnDeactivate = NO;
        panel.ignoresMouseEvents = YES;
        panel.level = NSPopUpMenuWindowLevel + 1;
        panel.collectionBehavior = NSWindowCollectionBehaviorCanJoinAllSpaces |
            NSWindowCollectionBehaviorIgnoresCycle | NSWindowCollectionBehaviorTransient;
        panel.contentView.wantsLayer = YES;
        panel.contentView.layer.magnificationFilter = kCAFilterNearest;
        panel.contentView.layer.minificationFilter = kCAFilterNearest;
        self.itemPreview = panel;
    }
    self.itemPreview.contentView.layer.contents = icon;
    [self.itemPreview setFrame:frame display:NO];
    if (!self.itemPreview.visible) [self.itemPreview orderFrontRegardless];
}

- (BOOL)canDropAt:(NSPoint)point {
    if (self.itemPalette.visible && NSPointInRect(point, self.itemPalette.frame)) return NO;
    for (NSScreen *screen in NSScreen.screens) {
        if (NSPointInRect(point, screen.visibleFrame) &&
            (self.hiddenDisplay == 0 || self.hiddenDisplay != displayId(screen).unsignedIntValue)) return YES;
    }
    return NO;
}

- (void)dropItem:(int)kind at:(NSPoint)point timestamp:(double)timestamp {
    [self.itemPreview orderOut:nil];
    if (self.itemPalette.visible && [self canDropAt:point])
        self.mouse(DMPlaceItem, point.x, primaryTop() - point.y, timestamp, kind, 0);
}

- (void)closeItemPalette {
    [self.itemPreview orderOut:nil];
    self.itemPreview = nil;
    [self.itemPalette orderOut:nil];
    self.itemPalette = nil;
}

- (void)closeItemPaletteButton:(__unused id)sender { [self closeItemPalette]; }

- (DMOverlay *)visibleOverlay {
    DMOverlay *primary = self.overlays[displayId(NSScreen.screens.firstObject)];
    if (primary.visible) return primary;
    for (DMOverlay *candidate in self.overlays.allValues)
        if (candidate.visible) return candidate;
    return nil;
}

- (void)captureKeyboard {
    DMOverlay *window = [self visibleOverlay];
    if (!window || self.capturing) return;
    self.capturing = YES;
    // A nonactivating panel can take the keyboard without stealing the other
    // app's activation. Keep controller input available for that key panel.
    GCController.shouldMonitorBackgroundEvents = YES;
    [window makeKeyAndOrderFront:nil];
    self.status.button.toolTip = [self label:@"正在控制 Madeline · Esc 释放键盘" english:@"Controlling Madeline · Esc releases keyboard"];
    self.key(-2, 1);
}

- (void)releaseKeyboard {
    if (!self.capturing) return;
    self.capturing = NO;
    GCController.shouldMonitorBackgroundEvents = NO;
    self.key(-2, 0);
    if ([NSApp.keyWindow isKindOfClass:DMOverlay.class]) [NSApp.keyWindow resignKeyWindow];
    self.status.button.toolTip = @"Desk Madeline";
}

- (void)windowDidResignKey:(NSNotification *)notification {
    if (![notification.object isKindOfClass:DMOverlay.class]) return;
    if (self.dragging && self.dragItem == 0) [self dragEvent:nil action:DMMouseCancel];
    [self releaseKeyboard];
}

- (void)dragEvent:(NSEvent *)event action:(int)action {
    NSPoint point = event.window ? [event.window convertPointToScreen:event.locationInWindow] : NSEvent.mouseLocation;
    if (action == DMMouseDown) {
        if (self.dragging) return;
        self.dragItem = self.hitItem;
        self.dragResize = self.dragItem != 0 && self.hoverHitMode == DMHitResize ? resizeAt(point, self.hoverEditFrame) : 0;
        self.dragging = YES;
        [CATransaction begin];
        [CATransaction setDisableActions:YES];
        self.dragImage.hidden = YES;
        [CATransaction commit];
        if (self.dragItem == 0) [self captureKeyboard];
    } else if (!self.dragging) return;
    if (action == DMMouseUp && self.dragItem != 0 && ![self canDropAt:point]) action = DMMouseCancel;
    self.mouse(action, point.x, primaryTop() - point.y, event ? event.timestamp : NSProcessInfo.processInfo.systemUptime,
        self.dragItem, self.dragResize);
    if (action == DMMouseUp || action == DMMouseCancel) {
        self.dragging = NO;
        self.dragItem = 0;
        self.dragResize = 0;
        self.hitItem = 0;
        self.dragPanel.ignoresMouseEvents = YES;
    }
}

- (void)deleteItemEvent:(NSEvent *)event {
    if (self.dragging || self.hitItem == 0) return;
    NSPoint point = [event.window convertPointToScreen:event.locationInWindow];
    self.mouse(DMDeleteItem, point.x, primaryTop() - point.y, event.timestamp, self.hitItem, 0);
    self.hitItem = 0;
    self.dragPanel.ignoresMouseEvents = YES;
    [self.dragPanel orderOut:nil];
}

- (void)setupDragPanel {
    NSPanel *panel = [[NSPanel alloc] initWithContentRect:NSZeroRect
        styleMask:NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel
        backing:NSBackingStoreBuffered defer:YES];
    panel.opaque = NO;
    panel.backgroundColor = NSColor.clearColor;
    panel.hasShadow = NO;
    panel.hidesOnDeactivate = NO;
    panel.ignoresMouseEvents = YES;
    panel.alphaValue = 0.05;
    panel.level = NSFloatingWindowLevel + 1;
    panel.collectionBehavior = NSWindowCollectionBehaviorCanJoinAllSpaces |
        NSWindowCollectionBehaviorFullScreenAuxiliary | NSWindowCollectionBehaviorStationary |
        NSWindowCollectionBehaviorIgnoresCycle;
    DMDragView *view = [[DMDragView alloc] initWithFrame:NSZeroRect];
    view.host = self;
    view.wantsLayer = YES;
    panel.contentView = view;
    self.dragImage = view.layer;
    self.dragImage.contentsGravity = kCAGravityResize;
    self.dragImage.magnificationFilter = kCAFilterNearest;
    self.dragImage.minificationFilter = kCAFilterNearest;
    self.dragPanel = panel;
}

- (void)applicationDidChangeScreenParameters:(NSNotification *)notification {
    if (self.dragging) [self dragEvent:nil action:DMMouseCancel];
    [self closeItemPalette];
    [self syncDisplays];
    self.windowSnapshot = nil;
    self.windowSnapshotGeneration++;
    if (self.displayLink || self.timer) [self startFrameClock];
}

- (void)syncDisplays {
    NSMutableSet<NSNumber *> *removed = [NSMutableSet setWithArray:self.overlays.allKeys];
    for (NSScreen *screen in NSScreen.screens) {
        NSNumber *idNumber = displayId(screen);
        if (!idNumber) continue;
        [removed removeObject:idNumber];
        DMOverlay *panel = self.overlays[idNumber];
        if (panel) {
            if (!NSEqualRects(panel.frame, screen.frame)) [panel setFrame:screen.frame display:NO];
            panel.portraitGroup.contentsScale = screen.backingScaleFactor;
            panel.portraitArtwork.contentsScale = screen.backingScaleFactor;
            continue;
        }
        panel = [[DMOverlay alloc] initWithContentRect:screen.frame
            styleMask:NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel
            backing:NSBackingStoreBuffered defer:NO screen:nil];
        panel.delegate = self;
        panel.opaque = NO;
        panel.backgroundColor = NSColor.clearColor;
        panel.hasShadow = NO;
        panel.hidesOnDeactivate = NO;
        panel.ignoresMouseEvents = YES;
        panel.level = NSFloatingWindowLevel;
        panel.releasedWhenClosed = NO;
        panel.collectionBehavior = NSWindowCollectionBehaviorCanJoinAllSpaces |
            NSWindowCollectionBehaviorFullScreenAuxiliary | NSWindowCollectionBehaviorStationary |
            NSWindowCollectionBehaviorIgnoresCycle;
        NSView *view = [[NSView alloc] initWithFrame:NSMakeRect(0, 0, NSWidth(screen.frame), NSHeight(screen.frame))];
        view.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        view.wantsLayer = YES;
        view.layer.masksToBounds = YES;
        panel.contentView = view;
        panel.sprite = addImageLayer(view.layer, kCAFilterNearest);
        panel.items = [NSMutableArray array];

        CALayer *portrait = [CALayer layer];
        portrait.hidden = YES;
        portrait.contentsScale = screen.backingScaleFactor;
        portrait.allowsGroupOpacity = YES;
        portrait.zPosition = 3;
        portrait.shadowColor = NSColor.blackColor.CGColor;
        portrait.shadowOpacity = .3f;
        portrait.shadowRadius = 0;
        [view.layer addSublayer:portrait];
        panel.portraitGroup = portrait;
        panel.portraitTail = addImageLayer(portrait, kCAFilterNearest);
        panel.portraitArtwork = addImageLayer(portrait, kCAFilterLinear);
        panel.portraitArtwork.contentsScale = screen.backingScaleFactor;
        panel.portraitBorder = addImageLayer(portrait, kCAFilterNearest);

        [panel setFrame:screen.frame display:NO];
        [panel orderFrontRegardless];
        self.overlays[idNumber] = panel;
    }
    for (NSNumber *removedId in removed) {
        DMOverlay *panel = self.overlays[removedId];
        if (panel.keyWindow) [self releaseKeyboard];
        [panel orderOut:nil];
        [self.overlays removeObjectForKey:removedId];
    }
}

- (NSEvent *)handleKey:(NSEvent *)event {
    if ([self recordBinding:event]) return nil;
    if (event.keyCode == kVK_Escape && event.type == NSEventTypeKeyDown && self.dragging && self.dragItem != 0) {
        [self dragEvent:nil action:DMMouseCancel];
        return nil;
    }
    if (event.keyCode == kVK_Escape && event.type == NSEventTypeKeyDown && self.itemPalette.visible) {
        [self closeItemPalette];
        return nil;
    }
    if (!self.capturing || ![NSApp.keyWindow isKindOfClass:DMOverlay.class]) return event;
    unsigned short code = event.keyCode;
    if (code == kVK_Escape && event.type == NSEventTypeKeyDown) {
        [self releaseKeyboard];
        return nil;
    }
    if (!secondaryKeyboardKey(code)) {
        NSUInteger binding = [self.keyboardCodes indexOfObject:@(code)];
        if (binding == NSNotFound) return event;
        code = defaultKeyboardCodes()[binding].unsignedShortValue;
    }
    if (event.type == NSEventTypeFlagsChanged) {
        if (code == kVK_Shift) self.key(code, (event.modifierFlags & NX_DEVICELSHIFTKEYMASK) != 0);
    } else if (event.type == NSEventTypeKeyUp) {
        self.key(code, 0);
    } else if (event.modifierFlags & (NSEventModifierFlagCommand | NSEventModifierFlagControl | NSEventModifierFlagOption)) {
        return event;
    } else if (!event.isARepeat) {
        self.key(code, 1);
    }
    return nil;
}

- (void)advanceFrameAt:(CFTimeInterval)now {
    if (self.choosing) return;
    CFTimeInterval elapsed = self.last > 0 ? now - self.last : 1.0 / 60;
    self.last = now;
    if (elapsed > 0) self.frame(elapsed);
}

- (void)displayFrame:(CADisplayLink *)link {
    [self advanceFrameAt:link.timestamp];
}

- (void)stopFrameClock {
    [self.displayLink invalidate];
    self.displayLink = nil;
    [self.timer invalidate];
    self.timer = nil;
    self.last = 0;
}

- (void)startFrameClock {
    [self stopFrameClock];
    NSScreen *screen = NSScreen.screens.firstObject;
    self.displayLink = [screen displayLinkWithTarget:self selector:@selector(displayFrame:)];
    if (self.displayLink) {
        float frames = MIN(60, screen.maximumFramesPerSecond);
        self.displayLink.preferredFrameRateRange = CAFrameRateRangeMake(frames, frames, frames);
        [self.displayLink addToRunLoop:NSRunLoop.mainRunLoop forMode:NSRunLoopCommonModes];
        return;
    }
    __weak DMHost *weakHost = self;
    self.timer = [NSTimer timerWithTimeInterval:1.0 / 120 repeats:YES block:^(__unused NSTimer *timer) {
        [weakHost advanceFrameAt:CACurrentMediaTime()];
    }];
    [NSRunLoop.mainRunLoop addTimer:self.timer forMode:NSRunLoopCommonModes];
}

- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    self.chinese = [NSLocale.preferredLanguages.firstObject hasPrefix:@"zh"];
    [self loadKeyBindings];
    self.overlays = [NSMutableDictionary dictionary];
    self.status = [NSStatusBar.systemStatusBar statusItemWithLength:NSVariableStatusItemLength];
    self.status.button.title = @"Desk Madeline";
    self.status.button.toolTip = @"Desk Madeline";
    self.menu = [[NSMenu alloc] initWithTitle:@"Desk Madeline"];
    self.menu.delegate = self;
    self.status.menu = self.menu;
    [self buildMenu];
    [self syncDisplays];
    [self setupDragPanel];
    GCController.shouldMonitorBackgroundEvents = NO;
    __weak DMHost *weakHost = self;
    self.keyMonitor = [NSEvent addLocalMonitorForEventsMatchingMask:
        NSEventMaskKeyDown | NSEventMaskKeyUp | NSEventMaskFlagsChanged
        handler:^NSEvent *(NSEvent *event) { return [weakHost handleKey:event]; }];
    if (self.start() == 0) {
        [NSApp terminate:nil];
        return;
    }
    [self startFrameClock];
    dispatch_async(dispatch_get_main_queue(), ^{ [weakHost captureKeyboard]; });
}

- (void)applicationWillTerminate:(NSNotification *)notification {
    [self stopFrameClock];
    [self closeItemPalette];
    if (self.keyMonitor) [NSEvent removeMonitor:self.keyMonitor];
    self.stop();
}
@end

int dm_run(DMStart start, DMFrame frame, DMKey key, DMMouse mouse, DMCommand command, DMStop stop) {
    @autoreleasepool {
        if (!NSThread.isMainThread) return 2;
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyAccessory];
        host = [DMHost new];
        host.start = start;
        host.frame = frame;
        host.key = key;
        host.mouse = mouse;
        host.command = command;
        host.stop = stop;
        NSApp.delegate = host;
        [NSApp run];
        return 0;
    }
}

static DMGamepadState readGamepad(GCExtendedGamepad *pad) {
    if (!pad) return (DMGamepadState){0};
    uint32_t buttons =
        (pad.dpad.up.isPressed ? DMUp : 0) | (pad.dpad.down.isPressed ? DMDown : 0) |
        (pad.dpad.left.isPressed ? DMLeft : 0) | (pad.dpad.right.isPressed ? DMRight : 0) |
        (pad.buttonA.isPressed ? DMCross : 0) | (pad.buttonB.isPressed ? DMCircle : 0) |
        (pad.buttonX.isPressed ? DMSquare : 0) | (pad.buttonY.isPressed ? DMTriangle : 0) |
        (pad.leftShoulder.isPressed ? DML1 : 0) | (pad.rightShoulder.isPressed ? DMR1 : 0);
    return (DMGamepadState){1, buttons, pad.leftThumbstick.xAxis.value, pad.leftThumbstick.yAxis.value,
        pad.leftTrigger.value, pad.rightTrigger.value};
}

void dm_gamepad(DMGamepadState *state) {
    GCExtendedGamepad *pad = nil;
    for (GCController *controller in GCController.controllers) {
        GCExtendedGamepad *candidate = controller.extendedGamepad;
        if ([candidate isKindOfClass:GCDualSenseGamepad.class]) {
            pad = candidate;
            break;
        }
        if (!pad) pad = candidate;
    }
    *state = readGamepad(pad);
}

int dm_displays(DMDisplayInfo *out, int capacity) {
    NSArray<NSScreen *> *screens = NSScreen.screens;
    CGFloat top = primaryTop();
    for (int i = 0; i < MIN((int)screens.count, capacity); i++) {
        NSScreen *screen = screens[i];
        out[i] = (DMDisplayInfo){displayId(screen).unsignedIntValue,
            quartzRect(screen.frame, top), quartzRect(screen.visibleFrame, top)};
    }
    return (int)screens.count;
}

static NSData *sampleWindows(void) {
    NSArray<NSDictionary *> *windows = CFBridgingRelease(CGWindowListCopyWindowInfo(
        kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements, kCGNullWindowID));
    NSMutableData *snapshot = [NSMutableData dataWithCapacity:windows.count * sizeof(DMWindowInfo)];
    pid_t ownPid = getpid();
    for (NSDictionary *window in windows) {
        int owner = [window[(__bridge NSString *)kCGWindowOwnerPID] intValue];
        if (owner == ownPid || owner <= 0) continue;
        if ([window[(__bridge NSString *)kCGWindowLayer] intValue] != 0 ||
            [window[(__bridge NSString *)kCGWindowAlpha] doubleValue] <= 0.01) continue;
        NSDictionary *raw = window[(__bridge NSString *)kCGWindowBounds];
        CGRect rect;
        if (!raw || !CGRectMakeWithDictionaryRepresentation((__bridge CFDictionaryRef)raw, &rect)) continue;
        if (rect.size.width < 24 || rect.size.height < 18) continue;
        uint32_t idNumber = [window[(__bridge NSString *)kCGWindowNumber] unsignedIntValue];
        DMWindowInfo info = {idNumber, owner, {rect.origin.x, rect.origin.y, rect.size.width, rect.size.height}};
        [snapshot appendBytes:&info length:sizeof(info)];
    }
    return snapshot;
}

int dm_windows(DMWindowInfo *out, int capacity, int *frontmost) {
    *frontmost = NSWorkspace.sharedWorkspace.frontmostApplication.processIdentifier;
    BOOL trackingDrag = (NSEvent.pressedMouseButtons & 1) != 0 && !host.dragging;
    // Read live bounds when a real window may be following the pointer, or
    // when changing apps can expose a different support surface.
    if (!host.windowSnapshot || trackingDrag || host.trackingWindowDrag || host.snapshotFrontmost != *frontmost) {
        host.windowSnapshotGeneration++;
        host.windowSnapshot = sampleWindows();
        host.snapshotFrontmost = *frontmost;
    } else if (!host.windowSampleInFlight) {
        host.windowSampleInFlight = YES;
        uint64_t generation = host.windowSnapshotGeneration;
        __weak DMHost *weakHost = host;
        dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
            @autoreleasepool {
                NSData *snapshot = sampleWindows();
                dispatch_async(dispatch_get_main_queue(), ^{
                    DMHost *strongHost = weakHost;
                    strongHost.windowSampleInFlight = NO;
                    if (strongHost.windowSnapshotGeneration == generation) strongHost.windowSnapshot = snapshot;
                });
            }
        });
    }
    host.trackingWindowDrag = trackingDrag;
    NSData *snapshot = host.windowSnapshot;
    int count = (int)(snapshot.length / sizeof(DMWindowInfo));
    if (count > 0 && capacity > 0) memcpy(out, snapshot.bytes, MIN(count, capacity) * sizeof(DMWindowInfo));
    return count;
}

static CGColorSpaceRef imageColorSpace(void) {
    static CGColorSpaceRef color;
    static dispatch_once_t once;
    dispatch_once(&once, ^{ color = CGColorSpaceCreateWithName(kCGColorSpaceSRGB); });
    return color;
}

static NSData *copyImagePixels(const void *pixels, int stride, int width, int height) {
    int imageStride = width * 4;
    if (stride == imageStride) return [NSData dataWithBytes:pixels length:(NSUInteger)imageStride * height];
    NSMutableData *copy = [NSMutableData dataWithLength:(NSUInteger)imageStride * height];
    uint8_t *output = copy.mutableBytes;
    const uint8_t *input = pixels;
    for (int row = 0; row < height; row++) {
        memcpy(output, input, imageStride);
        output += imageStride;
        input += stride;
    }
    return copy;
}

static CGImageRef imageFromPixels(NSData *pixels, int width, int height, BOOL interpolate) {
    CGDataProviderRef provider = CGDataProviderCreateWithCFData((__bridge CFDataRef)pixels);
    CGImageRef image = CGImageCreate(width, height, 8, 32, width * 4, imageColorSpace(),
        kCGBitmapByteOrder32Little | kCGImageAlphaPremultipliedFirst, provider, NULL, interpolate, kCGRenderingIntentDefault);
    CGDataProviderRelease(provider);
    return image;
}

static CGImageRef createImage(const void *pixels, int stride, int width, int height, BOOL interpolate) {
    return imageFromPixels(copyImagePixels(pixels, stride, width, height), width, height, interpolate);
}

static CGImageRef createLayerImage(DMImageLayer layer, BOOL interpolate) {
    return layer.pixels && layer.width > 0 && layer.height > 0
        ? createImage(layer.pixels, layer.stride, layer.width, layer.height, interpolate)
        : NULL;
}

static NSRect worldBounds(DMRect bounds, CGFloat top) {
    return NSMakeRect(bounds.x, top - bounds.y - bounds.height, bounds.width, bounds.height);
}

static void clearLayerImage(DMPixelLayer *layer) {
    if (layer.contents) layer.contents = nil;
    if (layer.imagePixels) layer.imagePixels = nil;
}

static BOOL sameLayerPixels(DMPixelLayer *layer, DMImageLayer source) {
    if (!layer.imagePixels || layer.pixelWidth != source.width || layer.pixelHeight != source.height) return NO;
    size_t rowBytes = (size_t)source.width * 4;
    const uint8_t *previous = layer.imagePixels.bytes;
    const uint8_t *current = source.pixels;
    if (rowBytes == (size_t)source.stride) return memcmp(previous, current, rowBytes * source.height) == 0;
    for (int row = 0; row < source.height; row++) {
        if (memcmp(previous, current, rowBytes) != 0) return NO;
        previous += rowBytes;
        current += source.stride;
    }
    return YES;
}

static id presentLayer(DMPixelLayer *layer, DMImageLayer source, NSRect world, NSRect panelFrame,
                       BOOL hidden, BOOL interpolate) {
    if (layer.backgroundColor) layer.backgroundColor = NULL;
    if (hidden || !source.pixels || source.width <= 0 || source.height <= 0 || !NSIntersectsRect(world, panelFrame)) {
        clearLayerImage(layer);
        return nil;
    }
    NSRect local = NSOffsetRect(world, -NSMinX(panelFrame), -NSMinY(panelFrame));
    if (!NSEqualRects(layer.frame, local)) layer.frame = local;
    // Position changes do not require a new image or compositor upload.
    if (!sameLayerPixels(layer, source)) {
        NSData *pixels = copyImagePixels(source.pixels, source.stride, source.width, source.height);
        id image = CFBridgingRelease(imageFromPixels(pixels, source.width, source.height, interpolate));
        layer.contents = image;
        layer.imagePixels = image ? pixels : nil;
        layer.pixelWidth = source.width;
        layer.pixelHeight = source.height;
    }
    return layer.contents;
}

static void presentHeartFill(DMPixelLayer *layer, DMImageLayer fill, NSRect world, NSRect panelFrame, BOOL flash) {
    clearLayerImage(layer);
    NSRect local = NSOffsetRect(world, -NSMinX(panelFrame), -NSMinY(panelFrame));
    if (!NSEqualRects(layer.frame, local)) layer.frame = local;
    CGFloat alpha = ((const uint8_t *)fill.pixels)[3] / 255.0;
    CGFloat value = flash ? 1.0 : 0.0;
    CGColorRef old = layer.backgroundColor;
    if (old && CGColorGetAlpha(old) == alpha && CGColorGetComponents(old)[0] == value) return;
    CGFloat components[] = {value, value, value, alpha};
    CGColorRef color = CGColorCreate(imageColorSpace(), components);
    layer.backgroundColor = color;
    CGColorRelease(color);
}

void dm_present(const void *pixels, int stride, int width, int height, float x, float y, int scale,
        uint32_t fullscreenDisplay, const DMPortraitLayer *portrait, int heartForeground) {
    host.hiddenDisplay = fullscreenDisplay;
    CGFloat top = primaryTop();
    DMImageLayer sprite = {pixels, stride, width, height, {x, y, width * scale, height * scale}};
    NSRect spriteWorld = worldBounds(sprite.bounds, top);
    NSRect borderWorld = worldBounds(portrait->border.bounds, top);
    NSRect artworkWorld = worldBounds(portrait->artwork.bounds, top);
    NSRect tailWorld = worldBounds(portrait->tail.bounds, top);
    NSRect portraitWorld = NSUnionRect(borderWorld, tailWorld);
    [CATransaction begin];
    [CATransaction setDisableActions:YES];
    for (NSNumber *idNumber in host.overlays) {
        DMOverlay *panel = host.overlays[idNumber];
        BOOL hidden = fullscreenDisplay != 0 && fullscreenDisplay == idNumber.unsignedIntValue;
        if (hidden) {
            if (panel.keyWindow) [host releaseKeyboard];
            if (panel.visible) [panel orderOut:nil];
        } else if (!panel.visible) {
            [panel orderFrontRegardless];
        }
        NSRect panelFrame = panel.frame;
        CGFloat spriteDepth = heartForeground ? 4 : 0;
        if (panel.sprite.zPosition != spriteDepth) panel.sprite.zPosition = spriteDepth;
        presentLayer(panel.sprite, sprite, spriteWorld, panelFrame, hidden, NO);
        BOOL portraitHidden = hidden || heartForeground || !portrait->border.pixels ||
            !portrait->artwork.pixels || !NSIntersectsRect(portraitWorld, panelFrame);
        if (panel.portraitGroup.hidden != portraitHidden) panel.portraitGroup.hidden = portraitHidden;
        if (!portraitHidden) {
            panel.portraitGroup.frame = NSOffsetRect(portraitWorld, -NSMinX(panelFrame), -NSMinY(panelFrame));
            panel.portraitGroup.shadowOffset = CGSizeMake(0, -scale);
            panel.portraitGroup.opacity = portrait->opacity;
        }
        presentLayer(panel.portraitTail, portrait->tail, tailWorld, portraitWorld, portraitHidden, NO);
        presentLayer(panel.portraitArtwork, portrait->artwork, artworkWorld, portraitWorld, portraitHidden, YES);
        presentLayer(panel.portraitBorder, portrait->border, borderWorld, portraitWorld, portraitHidden, NO);
    }
    [CATransaction commit];
}

static BOOL hitPixel(DMImageLayer layer, NSPoint point, CGFloat top, uint8_t minAlpha) {
    DMRect bounds = layer.bounds;
    if (!layer.pixels || bounds.width <= 0 || bounds.height <= 0) return NO;
    int column = (int)floor((point.x - bounds.x) * layer.width / bounds.width);
    int row = (int)floor((top - point.y - bounds.y) * layer.height / bounds.height);
    if (row < 0 || row >= layer.height || column < 0 || column >= layer.width) return NO;
    const uint8_t *pixels = layer.pixels;
    return pixels[row * layer.stride + column * 4 + 3] >= minAlpha;
}

void dm_present_items(const DMItemLayer *items, int count, uint32_t fullscreenDisplay) {
    // The heart's viewport fills share its managed backdrop and flash depths.
    const int heartBackdropDepth = 3, heartFlashDepth = 7;
    host.hoverItem = 0;
    host.hoverImage = nil;
    host.hoverHitMode = DMHitAlpha;
    if (count == 0 && !host.hasDesktopItems) return;
    host.hasDesktopItems = count > 0;
    @autoreleasepool {
        CGFloat top = primaryTop();
        NSPoint pointer = NSEvent.mouseLocation;
        NSInteger heartLevel = 1 + MAX(CGWindowLevelForKey(kCGDockWindowLevelKey),
            MAX(CGWindowLevelForKey(kCGMainMenuWindowLevelKey), CGWindowLevelForKey(kCGStatusWindowLevelKey)));
        [CATransaction begin];
        [CATransaction setDisableActions:YES];
        for (NSNumber *display in host.overlays) {
            DMOverlay *panel = host.overlays[display];
            NSUInteger index = 0;
            BOOL heartCoversDisplay = NO;
            if (fullscreenDisplay == 0 || fullscreenDisplay != display.unsignedIntValue) {
                for (int i = 0; i < count; i++) {
                    NSRect world = worldBounds(items[i].image.bounds, top);
                    if (!items[i].image.pixels || !NSIntersectsRect(world, panel.frame)) continue;
                    BOOL heartFill = items[i].itemId == 0 && items[i].image.width == 1 && items[i].image.height == 1 &&
                        (items[i].depth == heartBackdropDepth || items[i].depth == heartFlashDepth);
                    if (index == panel.items.count)
                        [panel.items addObject:addImageLayer(panel.contentView.layer, kCAFilterNearest)];
                    DMPixelLayer *layer = panel.items[index++];
                    if (layer.zPosition != items[i].depth) layer.zPosition = items[i].depth;
                    if (layer.opacity != items[i].opacity) layer.opacity = items[i].opacity;
                    if (heartFill) {
                        presentHeartFill(layer, items[i].image, world, panel.frame, items[i].depth == heartFlashDepth);
                        heartCoversDisplay |= NSContainsRect(NSInsetRect(world, -.5, -.5), panel.frame);
                        continue;
                    }
                    id image = presentLayer(layer, items[i].image, world, panel.frame, NO, NO);
                    if (!image) continue;
                    if (!host.dragging && items[i].itemId > 0 && NSPointInRect(pointer, panel.frame) &&
                        (host.hoverItem == 0 || items[i].depth >= host.hoverDepth)) {
                        int hitMode = items[i].hitMode;
                        NSRect edit = hitMode != DMHitAlpha ? worldBounds(items[i].hitBounds, top) : NSZeroRect;
                        NSRect target = hitMode == DMHitResize ? NSInsetRect(edit, -resizeBand, -resizeBand) : edit;
                        BOOL hit = hitMode == DMHitAlpha ? hitPixel(items[i].image, pointer, top, 180) : NSPointInRect(pointer, target);
                        if (hit) {
                            host.hoverItem = items[i].itemId;
                            host.hoverDepth = items[i].depth;
                            host.hoverFrame = hitMode == DMHitAlpha ? world : target;
                            host.hoverEditFrame = edit;
                            host.hoverHitMode = hitMode;
                            host.hoverImage = image;
                        }
                    }
                }
            }
            // Keep the whole scene at one window level so the heart, player and
            // orbs retain their relative depth while the menu bar and Dock dim.
            NSInteger level = heartCoversDisplay ? heartLevel : NSFloatingWindowLevel;
            if (panel.level != level) panel.level = level;
            while (panel.items.count > index) {
                [panel.items.lastObject removeFromSuperlayer];
                [panel.items removeLastObject];
            }
        }
        [CATransaction commit];
    }
}

// A single small input window serves the sprite under the cursor. It stays put
// during a drag so AppKit delivers the mouse-up even after the sprite has moved.
void dm_hit_target(const void *pixels, int stride, int width, int height, float x, float y, int scale) {
    if (host.dragging) return;
    NSPanel *panel = host.dragPanel;
    CGFloat top = primaryTop();
    NSPoint pointer = NSEvent.mouseLocation;
    DMImageLayer actor = {pixels, stride, width, height, {x, y, width * scale, height * scale}};
    BOOL actorHit = NO;
    if (hitPixel(actor, pointer, top, 1))
        for (DMOverlay *overlay in host.overlays.allValues)
            if (overlay.visible && NSPointInRect(pointer, overlay.frame)) { actorHit = YES; break; }
    BOOL itemHit = host.hoverItem != 0 && (!actorHit || host.hoverDepth >= 0);
    if ((!itemHit && !actorHit) || (host.itemPalette.visible && NSPointInRect(pointer, host.itemPalette.frame))) {
        host.hitItem = 0;
        setResizeCursor(0);
        panel.ignoresMouseEvents = YES;
        if (panel.visible) [panel orderOut:nil];
        return;
    }
    host.hitItem = itemHit ? host.hoverItem : 0;
    NSRect frame = itemHit ? host.hoverFrame : worldBounds(actor.bounds, top);
    BOOL editHit = itemHit && host.hoverHitMode != DMHitAlpha;
    id image = itemHit ? (editHit ? nil : host.hoverImage) : CFBridgingRelease(createLayerImage(actor, NO));
    [CATransaction begin];
    [CATransaction setDisableActions:YES];
    if (!NSEqualRects(panel.frame, frame)) [panel setFrame:frame display:NO];
    // One alpha level lets AppKit receive clicks on the transparent edges of
    // an inactive cassette block and on the rail's editable end.
    panel.backgroundColor = editHit ? [NSColor colorWithWhite:0 alpha:1.0 / 255.0] : NSColor.clearColor;
    host.dragImage.contents = image;
    host.dragImage.hidden = NO;
    [CATransaction commit];
    panel.ignoresMouseEvents = NO;
    if (!panel.visible) [panel orderFrontRegardless];
    setResizeCursor(itemHit && host.hoverHitMode == DMHitResize ? resizeAt(pointer, host.hoverEditFrame) : 0);
}

void dm_item_icon(int kind, const void *pixels, int stride, int width, int height) {
    CGImageRef image = pixels ? createImage(pixels, stride, width, height, NO) : NULL;
    if (!host.itemIcons) host.itemIcons = [NSMutableDictionary dictionary];
    host.itemIcons[@(kind)] = CFBridgingRelease(image);
}

void dm_status(int scale, int autonomy, int sound, int audioAvailable, int terrain, int rumble, int showExpressions) {
    host.scale = scale;
    host.autonomy = autonomy;
    host.sound = sound;
    host.audioAvailable = audioAvailable;
    host.terrain = terrain;
    host.rumble = rumble;
    host.showExpressions = showExpressions != 0;
}

void dm_assist_status(int count, int limit, int stamina, int dashAssist) {
    host.badelineCount = count;
    host.badelineLimit = limit;
    host.infiniteStamina = stamina != 0;
    host.dashAssist = dashAssist;
}

void dm_course_status(int state, const char *message, int focus) {
    host.courseState = state;
    if (focus) {
        [host closeItemPalette];
        [host captureKeyboard];
    }
    if (!message) return;
    NSScreen *screen = [host visibleOverlay].screen ?: NSScreen.screens.firstObject;
    if (!screen) return;
    [host.courseNotice orderOut:nil];
    NSRect display = screen.visibleFrame;
    CGFloat width = fmin(420, NSWidth(display) - 24), height = 40;
    NSPanel *notice = [[NSPanel alloc] initWithContentRect:NSMakeRect(NSMidX(display) - width / 2, NSMaxY(display) - height - 14, width, height)
        styleMask:NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel backing:NSBackingStoreBuffered defer:NO];
    notice.opaque = NO;
    notice.backgroundColor = NSColor.clearColor;
    notice.hasShadow = YES;
    notice.ignoresMouseEvents = YES;
    notice.level = NSPopUpMenuWindowLevel;
    DMItemPaletteView *background = [[DMItemPaletteView alloc] initWithFrame:NSMakeRect(0, 0, width, height)];
    NSTextField *text = [NSTextField labelWithString:[NSString stringWithUTF8String:message]];
    text.font = [NSFont systemFontOfSize:13 weight:NSFontWeightSemibold];
    text.alignment = NSTextAlignmentCenter;
    text.frame = NSMakeRect(10, 10, width - 20, 20);
    [background addSubview:text];
    notice.contentView = background;
    host.courseNotice = notice;
    [notice orderFrontRegardless];
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 2800 * NSEC_PER_MSEC), dispatch_get_main_queue(), ^{
        [notice orderOut:nil];
        if (host.courseNotice == notice) host.courseNotice = nil;
    });
}

void dm_cursor(float *x, float *y) {
    NSPoint point = NSEvent.mouseLocation;
    *x = point.x;
    *y = primaryTop() - point.y;
}

int dm_primary_button_down(void) {
    return (NSEvent.pressedMouseButtons & 1) != 0;
}

char *dm_pick_game(void) {
    [host releaseKeyboard];
    [NSApp activate];
    host.choosing = YES;
    NSOpenPanel *panel = [NSOpenPanel openPanel];
    panel.title = host.chinese ? @"选择 Celeste.app 或游戏目录" : @"Choose Celeste.app or the game folder";
    panel.canChooseFiles = YES;
    panel.canChooseDirectories = YES;
    panel.allowsMultipleSelection = NO;
    NSInteger result = [panel runModal];
    host.choosing = NO;
    host.last = 0;
    return result == NSModalResponseOK ? strdup(panel.URL.fileSystemRepresentation) : NULL;
}

void dm_free(void *buffer) { free(buffer); }

void dm_alert(const char *title, const char *message) {
    [NSApp activate];
    host.choosing = YES;
    NSAlert *alert = [[NSAlert alloc] init];
    alert.messageText = [NSString stringWithUTF8String:title];
    alert.informativeText = [NSString stringWithUTF8String:message];
    [alert runModal];
    host.choosing = NO;
    host.last = 0;
}

void dm_quit(void) { [NSApp terminate:nil]; }
