// Renderer.h - D3D11 device, flip-model swapchain and the composition thread.
#pragma once

#include "Common.h"
#include "SegmentShared.h"
#include "Presenter.h"
#include "PresenterSelect.h"

#include <dxgi1_5.h>
#include <d3d11.h>
#include <wrl/client.h>

#include <atomic>
#include <memory>
#include <thread>

namespace ds {

class Renderer {
public:
    Renderer() = default;
    ~Renderer();

    Renderer(const Renderer&) = delete;
    Renderer& operator=(const Renderer&) = delete;

    // Startup is deliberately in three steps so the presenter is chosen BEFORE
    // any window exists. Creating a fallback window first and swapping later
    // would flash a black window across the other displays every time the
    // specialized path wins.
    //
    //   1. InitDevice      - D3D11 device + DXGI factory (needs no window)
    //   2. DecidePresenter - waits for / acquires the specialized display, or
    //                        decides the window path. Returns the winner.
    //   3. FinishInit      - binds the chosen presenter to the window and
    //                        builds the pipeline.
    //
    // Must all complete before capture threads start (they share the device).
    bool InitDevice(const AppConfig& cfg, std::vector<SegmentShared>* segments);
    PresenterKind DecidePresenter(bool wantSpecialized, int waitSeconds,
                                  int frameDeadlineSeconds);
    bool FinishInit(HWND hwnd);
    void Shutdown();

    bool StartThread();
    void StopThread();

    // Called from the window thread when the physical monitor rect changes.
    void RequestResize(UINT width, UINT height);

    GpuContext* Gpu() { return &m_gpu; }

    // Diagnostic: draw a cycling solid colour instead of the captured segments,
    // so the presentation path can be proven with no capture involved at all.
    void SetTestPattern(bool on) { m_testPattern = on; }

    // Diagnostic mode override carried into PresenterInit.
    void SetModeOverride(bool lowest, uint32_t w, uint32_t h, double hz) {
        m_presenterOptions.lowestMode = lowest;
        m_presenterOptions.modeWidth = w;
        m_presenterOptions.modeHeight = h;
        m_presenterOptions.modeHz = hz;
    }

    // Which presentation path was chosen, and why.
    const PresenterSelection& Selection() const { return m_selection; }

    // True when the compositor was presenting and then lost the display. Maps
    // to exitcode::kDisplayLost so the control app can un-specialize.
    bool FatalRuntimeError() const {
        return m_fatalRuntimeError.load(std::memory_order_acquire);
    }

private:
    struct CursorGpu {
        Microsoft::WRL::ComPtr<ID3D11Texture2D>          tex;
        Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> srv;
        UINT     width = 0;
        UINT     height = 0;
        uint64_t version = 0;
    };

    bool CreateDevice();
    bool CreatePipeline();
    void RenderFrame();
    void DrawQuad(ID3D11ShaderResourceView* srv, float px, float py, float pw, float ph);
    void ThreadMain();
    void PaceFrame();

    // Records one presented frame for the content-live signal (see
    // m_contentLiveEvent): only a present that actually showed real captured
    // frames on every segment counts toward it.
    void NotePresented(bool hasContent, HRESULT hr);

    HWND        m_hwnd = nullptr;
    AppConfig   m_cfg;
    std::vector<SegmentShared>* m_segments = nullptr;

    GpuContext m_gpu;
    Microsoft::WRL::ComPtr<IDXGIFactory5>       m_factory;
    Microsoft::WRL::ComPtr<IDXGIAdapter1>       m_adapter;

    Microsoft::WRL::ComPtr<ID3D11VertexShader>  m_vs;
    Microsoft::WRL::ComPtr<ID3D11PixelShader>   m_ps;
    Microsoft::WRL::ComPtr<ID3D11Buffer>        m_cb;
    Microsoft::WRL::ComPtr<ID3D11SamplerState>  m_sampler;
    Microsoft::WRL::ComPtr<ID3D11BlendState>    m_blendOpaque;
    Microsoft::WRL::ComPtr<ID3D11BlendState>    m_blendAlpha;
    Microsoft::WRL::ComPtr<ID3D11RasterizerState> m_raster;

    std::vector<CursorGpu> m_cursors;

    bool m_testPattern = false;
    PresenterInit m_presenterOptions;
    std::unique_ptr<IPresenter> m_presenter;
    // Held between DecidePresenter and FinishInit when the specialized path won.
    std::unique_ptr<IPresenter> m_acquiredSpecialized;
    PresenterSelection          m_selection;

    std::atomic<bool> m_stop{false};
    std::atomic<bool> m_fatalRuntimeError{false};
    std::thread       m_thread;

    LARGE_INTEGER m_qpcFreq = {};
    long long     m_nextPresentQpc = 0;
    HANDLE        m_paceTimer = nullptr;

    // "Content live": signalled once real captured frames have been presented
    // on every segment for several consecutive frames. Unlike a first Present
    // (which can be an all-black clear when no capture has delivered yet),
    // this proves the user can actually see the split content — it is what
    // the control app's auto-keep waits for.
    HANDLE m_contentLiveEvent = nullptr;
    bool   m_contentLiveSignalled = false;
    int    m_contentStreak = 0;
};

} // namespace ds
