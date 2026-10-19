-- Routes libmpv mouse input to Dino Player's main client.  The embedded
-- native video surface consumes pointer events before Avalonia can see them.

local function send_input(kind, delta)
    local position = mp.get_property_native("mouse-pos") or {}
    local dimensions = mp.get_property_native("osd-dimensions") or {}
    local x = position.x or 0
    local y = position.y or 0
    local height = dimensions.h or 0

    mp.commandv("script-message-to", "main", "dinoplayer-input", kind,
        tostring(x), tostring(y), tostring(height), tostring(delta or 0))
end

mp.add_forced_key_binding("MOUSE_MOVE", "dinoplayer_mouse_move", function()
    send_input("move")
end, { repeatable = true })

mp.add_forced_key_binding("MOUSE_ENTER", "dinoplayer_mouse_enter", function()
    send_input("enter")
end)

mp.add_forced_key_binding("MOUSE_LEAVE", "dinoplayer_mouse_leave", function()
    send_input("leave")
end)

mp.add_forced_key_binding("MBTN_LEFT_DBL", "dinoplayer_mouse_double_click", function()
    send_input("double-click")
end)

mp.add_forced_key_binding("MBTN_RIGHT", "dinoplayer_mouse_right_click", function()
    send_input("right-click")
end)

mp.add_forced_key_binding("WHEEL_UP", "dinoplayer_wheel_up", function()
    send_input("wheel", 1)
end)

mp.add_forced_key_binding("WHEEL_DOWN", "dinoplayer_wheel_down", function()
    send_input("wheel", -1)
end)
